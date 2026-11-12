package cn.feiyu.console;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.graphics.drawable.BitmapDrawable;
import android.view.MotionEvent;
import android.view.GestureDetector;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapShader;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Shader;
import android.graphics.BitmapFactory;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.Editable;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URLEncoder;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * 肥鱼控制台 · 原生安卓版（不使用 WebView）
 * 数据全部来自服务器 8090 的 JSON 接口；连不上时显示明确错误 + 上次缓存，不会白屏。
 */
public class MainActivity extends Activity {

    // ==== 服务器配置（改这里即可换服务器）====
    static String BASE = "";    // 首次启动填，存 SharedPreferences("fatbot")
    static String TOKEN = "";   // 同上

    // ==== 配色 ====
    static final int C_BG = 0xFF141518;
    static final int C_CARD = 0xFF1D1F23;
    static final int C_LINE = 0xFF2C2F35;
    static final int C_TXT = 0xFFE8EAED;
    static final int C_SUB = 0xFF9AA3AF;
    static final int C_OK = 0xFF3FC878;
    static final int C_BAD = 0xFFEF5B60;
    static final int C_ACCENT = 0xFF4A7BFF;
    static final int C_ACCENT_D = 0xFF2B3D6B;
    static final int C_BTN = 0xFF22252A;

    final ExecutorService pool = Executors.newFixedThreadPool(3);
    final Handler ui = new Handler(Looper.getMainLooper());
    SharedPreferences prefs;

    LinearLayout root, pageHome, pageCtl, pageSelf, pageQr, pageLogs, pageQQ, pageMem;
    final TextView[] tabViews = new TextView[7];

    // 鱼的记忆（2026-11-07）
    LinearLayout memList;
    TextView memWho, memStat, memRecallOut;
    EditText memQ;
    String memPersona = "feiyu", memChat = "";
    final java.util.List<JSONObject> memChats = new java.util.ArrayList<JSONObject>();

    // 她的 QQ（资料 + 说说）
    ImageView qqAva;
    TextView qqName, qqNum, qqSign, qqStat, qqListHint;
    EditText qqNickIn, qqSignIn, qqPostIn;
    LinearLayout qqList;
    String qqAvaB64 = "";
    final List<byte[]> qqPostImgs = new ArrayList<byte[]>();   // 待发图片（已压好的 JPEG）
    LinearLayout qqImgStrip;
    static final int REQ_PICK_AVA = 0x7060;
    static final int REQ_PICK_QIMG = 0x7061;

    // 机器人控件（每群）
    Spinner ctlSpinner;
    Button ctlAtBtn;
    SeekBar ctlSpeedBar, ctlRateBar;
    TextView ctlSpeedVal, ctlRateVal, ctlLenVal, ctlStatus, ctlGroupTip;
    Button ctlLen14, ctlLen26, ctlLen40, ctlRateClear;
    Button[] ctlPokeBtns;           // 戳一戳回复概率：11 个挡位按钮
    TextView ctlPokeVal;
    double ctlPoke = 1.0;
    // 音色风格（全局，不属于某个群）
    Button ctlVoiceOrig, ctlVoiceSoft;
    TextView ctlVoiceVal;
    String ctlVoice = "diona";
    // 沉默名单（拉黑别的机器人，全局）
    LinearLayout blList;
    TextView blHint;
    Button blToggle;
    EditText blInput, blNote;
    boolean blOpen = false;
    final List<JSONObject> blItems = new ArrayList<JSONObject>();   // 每项 {uid,note,ts,removed?}
    // 戳一戳概率挡位（和电脑端一致）：1% 起步，中间每 10% 一档，最高 100%
    static final double[] CTL_POKE_STEPS = {
            0.01, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 1.00
    };
    // 默默发表情挡位：0 = 这个群关掉；-1 = 用插件默认（3%），由「用默认」按钮设置
    static final double[] CTL_AMBIENT_STEPS = {
            0.0, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15, 0.20, 0.30, 0.50
    };
    Button[] ctlAmbientBtns;
    Button ctlAmbientFollow;
    TextView ctlAmbientVal;
    double ctlAmbient = -1;
    String ctlChat = null;
    boolean ctlOnlyAt = false;
    int ctlAi = -1;                 // C1：-1=跟随全局  1=本群开  0=本群关
    Button ctlAiBtn;
    int ctlBurst = -1;              // 刷屏惊讶（2026-10-28）：-1=跟随全局 1=本群开 0=本群关
    Button ctlBurstBtn;
    int ctlLen = 26;
    double ctlSpeed = 1.0;
    double ctlRate = -1.0;
    final List<String> ctlGids = new ArrayList<String>();
    final List<String> ctlNames = new ArrayList<String>();
    int curTab = 0;

    TextView vVerdict, vHint, vSvc, vNap, vMem, vUp, vFlow, vStamp, vDog;
    View dot;

    TextView sVerdict, sHint, sItems;
    View sDot;

    ImageView qrImage;
    TextView qrTip;

    TextView runBox;
    LinearLayout chatBox;
    LinearLayout convBox;
    ScrollView chatScroll, runScroll;
    EditText chatInput;
    boolean sendAsVoice = false;
    TextView voiceToggle;
    // @ / 引用
    LinearLayout composeBar;                    // 输入框上方：引用条 + @ 芯片
    JSONObject quoteMsg = null;                 // 正在引用的那条消息
    final List<JSONObject> atList = new ArrayList<JSONObject>();
    final List<JSONObject> memberCache = new ArrayList<JSONObject>();
    String memberCacheChat = null;
    final String[] QUICK = {"我回来啦", "我去吃饭了", "晚安", "在忙吗"};
    LinearLayout convCard, chatCardView;
    String chatTarget = null;      // 会话 key：「群 123456」/「私聊 789」
    String chatTitle = null;       // 显示用的名字（群名 / 好友昵称）
    Runnable chatPoll = null;
    ScrollView rootScroll;
    boolean logMsg = true;      // true=聊天气泡  false=运行日志
    boolean scrollOnNextLoad = false;   // 刚打开会话：这一轮渲染完必须贴到底
    boolean stickBottom = true;         // 让视图一直跟着最新一条（用户自己往上翻就关掉）
    TextView logTabMsg, logTabRun;

    Runnable qrTimer;
    Runnable statusTimer;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        prefs = getSharedPreferences("fish", MODE_PRIVATE);
        getWindow().setStatusBarColor(C_BG);
        getWindow().setNavigationBarColor(C_BG);
        buildUi();
        switchTab(0);
        loadStatus();
        statusTimer = new Runnable() {
            public void run() {
                if (curTab == 0) loadStatus();
                ui.postDelayed(this, 15000);
            }
        };
        ui.postDelayed(statusTimer, 15000);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        stopQrTimer();
        if (statusTimer != null) ui.removeCallbacks(statusTimer);
        pool.shutdownNow();
    }

    // ================================================== UI 构建

    // ======================================================== 公开版首次配置
    // 源码里不带任何服务器地址/Token；第一次打开弹一趟，填完存本机 SharedPreferences。
    void ensureConfigured() {
        android.content.SharedPreferences sp = getSharedPreferences("fatbot", MODE_PRIVATE);
        BASE  = sp.getString("base", "");
        TOKEN = sp.getString("token", "");
        if (BASE.length() > 0 && TOKEN.length() > 0) return;

        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(8), dp(16), dp(8));
        final EditText eBase = plInput("服务器地址，如 http://1.2.3.4:8090");
        final EditText eTok  = plInput("控制台 Token");
        eBase.setText(BASE);
        eTok.setText(TOKEN);
        box.addView(eBase);
        box.addView(eTok);

        new AlertDialog.Builder(this)
                .setTitle("首次配置")
                .setView(box)
                .setCancelable(false)
                .setPositiveButton("保存", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        String b = eBase.getText().toString().trim();
                        while (b.endsWith("/")) b = b.substring(0, b.length() - 1);
                        BASE  = b;
                        TOKEN = eTok.getText().toString().trim();
                        getSharedPreferences("fatbot", MODE_PRIVATE).edit()
                                .putString("base", BASE).putString("token", TOKEN).apply();
                        recreate();
                    }
                })
                .show();
    }
    void buildUi() {
        ensureConfigured();
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(C_BG);
        scroll.setFillViewport(true);

        root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(C_BG);
        int pad = dp(14);
        root.setPadding(pad, dp(18) + statusBarHeight(), pad, dp(24));

        root.addView(text("肥鱼控制台", 26, C_TXT, true));
        TextView sub = text("原生安卓版 · 协议端 SnowLuma", 13, C_SUB, false);
        sub.setPadding(0, dp(2), 0, dp(14));
        root.addView(sub);

        LinearLayout tabs = new LinearLayout(this);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        String[] names = {"状态", "控件", "自检", "扫码", "日志", "QQ", "记忆"};
        for (int i = 0; i < names.length; i++) {
            final int idx = i;
            TextView t = new TextView(this);
            t.setText(names[i]);
            t.setTextSize(14);
            t.setGravity(Gravity.CENTER);
            t.setTypeface(Typeface.DEFAULT_BOLD);
            t.setPadding(0, dp(14), 0, dp(14));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
            lp.setMargins(dp(3), 0, dp(3), dp(12));
            t.setLayoutParams(lp);
            t.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { switchTab(idx); }
            });
            tabViews[i] = t;
            tabs.addView(t);
        }
        root.addView(tabs);

        pageHome = buildHome();
        pageCtl = buildCtl();
        pageSelf = buildSelf();
        pageQr = buildQr();
        pageLogs = buildLogs();
        pageQQ = buildQQProfile();
        pageMem = buildMem();
        root.addView(pageHome);
        root.addView(pageCtl);
        root.addView(pageSelf);
        root.addView(pageQr);
        root.addView(pageLogs);
        root.addView(pageQQ);
        root.addView(pageMem);

        scroll.addView(root, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        rootScroll = scroll;
        setContentView(scroll);
        // 键盘弹出/收起、转屏都会改可视高度：聊天区跟着重算，输入框始终留在屏幕里
        scroll.addOnLayoutChangeListener(new View.OnLayoutChangeListener() {
            public void onLayoutChange(View v, int l, int t, int r, int b,
                                       int ol, int ot, int or2, int ob) {
                if (curTab == 4 && chatCardView != null
                        && chatCardView.getVisibility() == View.VISIBLE) {
                    fitChatHeight();
                }
            }
        });
    }

    LinearLayout buildHome() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);

        LinearLayout card = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        dot = new View(this);
        LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(12), dp(12));
        dlp.setMargins(0, 0, dp(10), 0);
        dot.setLayoutParams(dlp);
        setDot(dot, false);
        head.addView(dot);
        LinearLayout hv = new LinearLayout(this);
        hv.setOrientation(LinearLayout.VERTICAL);
        vVerdict = text("检查中…", 21, C_TXT, true);
        vHint = text("", 13, C_SUB, false);
        hv.addView(vVerdict);
        hv.addView(vHint);
        head.addView(hv);
        hv.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { probe(); }
        });
        card.addView(head);

        LinearLayout kv = new LinearLayout(this);
        kv.setOrientation(LinearLayout.VERTICAL);
        kv.setPadding(0, dp(12), 0, 0);
        vSvc = addRow(kv, "服务");
        vNap = addRow(kv, "协议端");
        vMem = addRow(kv, "内存");
        vUp = addRow(kv, "启动于");
        vFlow = addRow(kv, "最近收发");
        card.addView(kv);
        page.addView(card);

        LinearLayout c2 = card();
        LinearLayout h2 = new LinearLayout(this);
        h2.setOrientation(LinearLayout.HORIZONTAL);
        h2.setGravity(Gravity.CENTER_VERTICAL);
        TextView t2 = text("控制", 16, C_TXT, true);
        LinearLayout.LayoutParams t2lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        t2.setLayoutParams(t2lp);
        h2.addView(t2);
        vStamp = text("--:--:--", 12, C_SUB, false);
        vStamp.setPadding(dp(10), dp(4), dp(10), dp(4));
        vStamp.setBackground(roundBg(C_BTN, C_LINE, 20));
        h2.addView(vStamp);
        c2.addView(h2);
        c2.addView(buttonRow(
                btn("启动服务", C_ACCENT, 0xFFFFFFFF, "start"),
                btn("重启服务", 0, 0, "restart")));
        c2.addView(buttonRow(
                btn("关闭（告别）", C_BAD, 0xFFFFFFFF, "stop"),
                btn("刷新状态", 0, 0, "refresh")));
        page.addView(c2);

        LinearLayout c3 = card();
        c3.addView(text("协议端 · SnowLuma", 16, C_TXT, true));
        c3.addView(buttonRow(
                btn("重启协议端", 0, 0, "napcat-restart"),
                btn("打开 WebUI", 0, 0, "webui")));
        TextView tip = text("掉线（被踢/要重新登录）时，去「扫码」页扫一下。", 12, C_SUB, false);
        tip.setPadding(0, dp(8), 0, 0);
        c3.addView(tip);
        page.addView(c3);

        // C2：花名册（线上文档样式，用浏览器打开）
        LinearLayout c35 = card();
        c35.addView(text("花名册", 16, C_TXT, true));
        TextView rTip = text("名字 / 好感度 / 说话方式，文档样式排版，可搜索。", 12, C_SUB, false);
        rTip.setPadding(0, dp(4), 0, dp(2));
        c35.addView(rTip);
        c35.addView(buttonRow(
                btn("打开花名册", C_ACCENT, 0xFFFFFFFF, "roster"),
                btn("复制链接", 0, 0, "roster-copy")));
        page.addView(c35);

        LinearLayout c4 = card();
        c4.addView(text("看门狗", 16, C_TXT, true));
        vDog = text("每 3 分钟检查一次：掉线自动重启协议端。", 13, C_SUB, false);
        vDog.setPadding(0, dp(6), 0, 0);
        c4.addView(vDog);
        page.addView(c4);
        page.addView(buildTokenCard());   // 每日 token 用量（总览常显，明细折叠）
        return page;
    }


    // ============================================================ 每日 token 用量
    TextView tkStatus, tkToggle;
    LinearLayout tkInner, tkList;
    Runnable tkTicker;

    LinearLayout buildTokenCard() {
        LinearLayout c = card();
        c.addView(text("她每天用了多少 token", 16, C_TXT, true));
        tkStatus = text("读取中…", 13, C_TXT, true);
        tkStatus.setPadding(0, dp(6), 0, 0);
        c.addView(tkStatus);

        tkToggle = text("展开 7 天明细 ▾", 12, C_SUB, false);
        tkToggle.setPadding(0, dp(8), 0, dp(4));
        c.addView(tkToggle);

        tkInner = new LinearLayout(this);
        tkInner.setOrientation(LinearLayout.VERTICAL);
        tkInner.setVisibility(View.GONE);
        c.addView(tkInner);

        tkList = new LinearLayout(this);
        tkList.setOrientation(LinearLayout.VERTICAL);
        tkInner.addView(tkList);

        Button rf = btn("刷新用量", 0, C_TXT, "");
        LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(dp(104), dp(40));
        rlp.setMargins(0, dp(6), 0, dp(2));
        rf.setLayoutParams(rlp);
        rf.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { tkLoad(); }
        });
        tkInner.addView(rf);

        TextView tip = text("口径：每次 AI 调用的 usage 累加（含缓存命中），从 2026-10-22 开始统计。",
                10.5f > 0 ? 11 : 11, C_SUB, false);
        tip.setPadding(0, dp(4), 0, 0);
        tkInner.addView(tip);

        tkToggle.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = tkInner.getVisibility() != View.VISIBLE;
                tkInner.setVisibility(show ? View.VISIBLE : View.GONE);
                tkToggle.setText(show ? "收起明细 ▴" : "展开 7 天明细 ▾");
            }
        });
        tkLoad();
        // 60 秒自动刷新（看着像实时；一次就一个 HTTP 请求）
        if (tkTicker == null) {
            tkTicker = new Runnable() {
                public void run() {
                    try { if (tkStatus != null && tkStatus.isShown()) tkLoad(); } catch (Exception e) { }
                    ui.postDelayed(this, 60000);
                }
            };
            ui.postDelayed(tkTicker, 60000);
        }
        return c;
    }

    static String fmtTok(long n) {
        if (n >= 1000000L) return String.format(java.util.Locale.US, "%.2fM", n / 1000000.0);
        if (n >= 1000L) return String.format(java.util.Locale.US, "%.1fk", n / 1000.0);
        return String.valueOf(n);
    }

    void tkLoad() {
        if (tkStatus != null) tkStatus.setText("读取中…");
        getJson("/api/tokens?days=7", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (tkStatus != null) tkStatus.setText("用量读取失败：" + err);
                    return;
                }
                tkPaint(o);
            }
        });
    }

    void tkPaint(JSONObject o) {
        if (o == null || tkList == null) return;
        JSONObject today = o.optJSONObject("today");
        long tin = today == null ? 0 : today.optLong("in");
        long tout = today == null ? 0 : today.optLong("out");
        long tcache = today == null ? 0 : today.optLong("cached");
        long tcalls = today == null ? 0 : today.optLong("calls");
        if (tkStatus != null)
            tkStatus.setText("今天 " + fmtTok(tin + tout) + " token（输入 " + fmtTok(tin)
                    + " / 输出 " + fmtTok(tout) + " / 缓存 " + fmtTok(tcache) + "）· "
                    + tcalls + " 次调用");

        tkList.removeAllViews();
        JSONArray days = o.optJSONArray("days");
        if (days == null || days.length() == 0) return;
        long maxv = 1;
        for (int i = 0; i < days.length(); i++) {
            JSONObject d = days.optJSONObject(i);
            if (d == null) continue;
            long tot = d.optLong("in") + d.optLong("out");
            if (tot > maxv) maxv = tot;
        }
        for (int i = 0; i < days.length(); i++) {
            JSONObject d = days.optJSONObject(i);
            if (d == null) continue;
            String date = d.optString("date", "");
            long tot = d.optLong("in") + d.optLong("out");
            long calls = d.optLong("calls");
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(0, dp(3), 0, dp(3));

            TextView dl = text(date.length() >= 5 ? date.substring(5) : date, 11, C_SUB, false);
            dl.setLayoutParams(new LinearLayout.LayoutParams(dp(46), ViewGroup.LayoutParams.WRAP_CONTENT));
            row.addView(dl);

            // 条形：两个按权重分（不依赖测量就能画）
            LinearLayout bar = new LinearLayout(this);
            bar.setOrientation(LinearLayout.HORIZONTAL);
            LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(0, dp(10), 1f);
            blp.setMargins(0, 0, dp(8), 0);
            bar.setLayoutParams(blp);
            double ratio = tot <= 0 ? 0.0 : Math.max(0.02, (double) tot / (double) maxv);
            View fill = new View(this);
            fill.setBackground(roundBg(C_ACCENT, 0, 5));
            bar.addView(fill, new LinearLayout.LayoutParams(0, dp(10), (float) ratio));
            View tail = new View(this);
            tail.setBackground(roundBg(C_BTN, 0, 5));
            bar.addView(tail, new LinearLayout.LayoutParams(0, dp(10), (float) Math.max(0.0001, 1 - ratio)));
            row.addView(bar);

            TextView vl = text(fmtTok(tot) + (calls > 0 ? " · " + calls + "次" : ""), 10.5f > 0 ? 11 : 11, C_SUB, false);
            vl.setGravity(Gravity.RIGHT);
            vl.setLayoutParams(new LinearLayout.LayoutParams(dp(96), ViewGroup.LayoutParams.WRAP_CONTENT));
            row.addView(vl);
            tkList.addView(row);
        }
    }

    LinearLayout buildSelf() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        LinearLayout card = card();

        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        sDot = new View(this);
        LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(12), dp(12));
        dlp.setMargins(0, 0, dp(10), 0);
        sDot.setLayoutParams(dlp);
        setDot(sDot, false);
        head.addView(sDot);
        LinearLayout hv = new LinearLayout(this);
        hv.setOrientation(LinearLayout.VERTICAL);
        sVerdict = text("点「开始自检」", 21, C_TXT, true);
        sHint = text("检查服务 / 容器 / 登录 / 连接 / 收发 / 看门狗", 13, C_SUB, false);
        hv.addView(sVerdict);
        hv.addView(sHint);
        head.addView(hv);
        card.addView(head);

        sItems = text("", 14, C_TXT, false);
        sItems.setPadding(0, dp(14), 0, 0);
        sItems.setLineSpacing(dp(6), 1f);
        card.addView(sItems);

        card.addView(buttonRow(
                btn("开始自检", C_ACCENT, 0xFFFFFFFF, "selfcheck"),
                btn("一键修复", 0, 0, "fix")));
        page.addView(card);
        return page;
    }

    LinearLayout buildQr() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        LinearLayout card = card();
        card.addView(text("登录二维码", 16, C_TXT, true));

        qrImage = new ImageView(this);
        LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(330));
        ilp.setMargins(0, dp(10), 0, dp(6));
        qrImage.setLayoutParams(ilp);
        qrImage.setScaleType(ImageView.ScaleType.FIT_CENTER);
        qrImage.setBackground(roundBg(0xFFFFFFFF, 0, 16));
        card.addView(qrImage);

        qrTip = text("每 20 秒自动换新；用手机 QQ → 扫一扫。", 12, C_SUB, false);
        card.addView(qrTip);

        card.addView(buttonRow(
                btn("刷新二维码", C_ACCENT, 0xFFFFFFFF, "qr"),
                btn("重启协议端", 0, 0, "napcat-restart")));
        page.addView(card);
        return page;
    }

    // ================================================== 机器人控件页
    LinearLayout buildCtl() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);

        // 选群
        LinearLayout c1 = card();
        c1.addView(text("选一个群", 16, C_TXT, true));
        ctlSpinner = new Spinner(this);
        ctlSpinner.setBackground(roundBg(C_BTN, C_LINE, 12));
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        sp.setMargins(0, dp(10), 0, dp(6));
        ctlSpinner.setLayoutParams(sp);
        ctlSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            public void onItemSelected(AdapterView<?> a, View v, int pos, long id) {
                if (pos >= 0 && pos < ctlGids.size()) {
                    ctlChat = "群 " + ctlGids.get(pos);
                    ctlLoadRules(ctlChat);
                }
            }
            public void onNothingSelected(AdapterView<?> a) { }
        });
        c1.addView(ctlSpinner);
        ctlGroupTip = text("下拉选群；也可以直接在下面输入群号", 12, C_SUB, false);
        c1.addView(ctlGroupTip);
        final EditText gidInput = new EditText(this);
        gidInput.setHint("群号，例如 123456789");
        gidInput.setTextSize(14);
        gidInput.setTextColor(C_TXT);
        gidInput.setHintTextColor(C_SUB);
        gidInput.setBackground(roundBg(C_BTN, C_LINE, 12));
        gidInput.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams gi = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        gi.setMargins(0, dp(8), 0, 0);
        gidInput.setLayoutParams(gi);
        c1.addView(gidInput);
        c1.addView(buttonRow(
                btn("刷新群列表", C_ACCENT, 0xFFFFFFFF, "ctl-groups"),
                btn("载入这个群号", 0, 0, "ctl-loadgid")));
        page.addView(c1);

        // 三个控件
        LinearLayout c2 = card();
        c2.addView(text("她在这个群怎么说话", 16, C_TXT, true));

        // 仅@后回复
        LinearLayout rAt = new LinearLayout(this);
        rAt.setOrientation(LinearLayout.HORIZONTAL);
        rAt.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams rl = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        rl.setMargins(0, dp(12), 0, 0);
        rAt.setLayoutParams(rl);
        TextView lAt = text("仅接受 @ 后回复", 15, C_TXT, false);
        lAt.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        rAt.addView(lAt);
        ctlAtBtn = btn("关闭", 0, 0, "ctl-at");
        LinearLayout.LayoutParams bl = new LinearLayout.LayoutParams(dp(96), dp(44));
        ctlAtBtn.setLayoutParams(bl);
        rAt.addView(ctlAtBtn);
        c2.addView(rAt);

        // C1：AI 对话开关（跟随全局 / 开 / 关 —— 只影响这个群）
        LinearLayout rAi = new LinearLayout(this);
        rAi.setOrientation(LinearLayout.HORIZONTAL);
        rAi.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams rai = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        rai.setMargins(0, dp(12), 0, 0);
        rAi.setLayoutParams(rai);
        TextView lAi = text("AI 对话（本群）", 15, C_TXT, false);
        lAi.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        rAi.addView(lAi);
        ctlAiBtn = btn("跟随全局", 0, 0, "ctl-ai");
        LinearLayout.LayoutParams bai = new LinearLayout.LayoutParams(dp(106), dp(44));
        ctlAiBtn.setLayoutParams(bai);
        ctlAiBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                ctlAi = (ctlAi == -1) ? 1 : (ctlAi == 1 ? 0 : -1);   // 跟随 → 开 → 关 → 跟随
                ctlPaint();
            }
        });
        rAi.addView(ctlAiBtn);
        c2.addView(rAi);
        c2.addView(text("选「关」这个群就只听词库说话，别的群不受影响；「跟随全局」= 按 /ai on|off 的总开关",
                12, C_SUB, false));

        // 刷屏惊讶（2026-10-28 用户：「那个检测一个人发特别多的消息的时候发的话」太吵）
        // 三态和 AI 一样：跟随全局 → 本群开 → 本群关
        LinearLayout rBurst = new LinearLayout(this);
        rBurst.setOrientation(LinearLayout.HORIZONTAL);
        rBurst.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams rbi = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        rbi.setMargins(0, dp(12), 0, 0);
        rBurst.setLayoutParams(rbi);
        TextView lBurst = text("刷屏惊讶（本群）", 15, C_TXT, false);
        lBurst.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        rBurst.addView(lBurst);
        ctlBurstBtn = btn("跟随全局", 0, 0, "ctl-burst");
        ctlBurstBtn.setLayoutParams(new LinearLayout.LayoutParams(dp(106), dp(44)));
        ctlBurstBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                ctlBurst = (ctlBurst == -1) ? 1 : (ctlBurst == 1 ? 0 : -1);   // 跟随 → 开 → 关 → 跟随
                ctlPaint();
            }
        });
        rBurst.addView(ctlBurstBtn);
        c2.addView(rBurst);
        c2.addView(text("一个人一阵子发好多条时，她会说「哇，一口气这么多条，我眼睛都看花了」。选「本群关」这个群就不说了。",
                12, C_SUB, false));

        // 回复速度
        TextView lSp = text("回复速度", 15, C_TXT, false);
        lSp.setPadding(0, dp(14), 0, 0);
        c2.addView(lSp);
        ctlSpeedBar = new SeekBar(this);
        ctlSpeedBar.setMax(270);      // 30..300 → 0.30x..3.00x
        ctlSpeedBar.setProgress(70);
        ctlSpeedBar.setPadding(0, dp(6), 0, dp(6));
        c2.addView(ctlSpeedBar);
        ctlSpeedVal = text("1.00x", 13, C_ACCENT, true);
        c2.addView(ctlSpeedVal);

        // 聊天频率
        TextView lRt = text("聊天频率", 15, C_TXT, false);
        lRt.setPadding(0, dp(14), 0, 0);
        c2.addView(lRt);
        ctlRateBar = new SeekBar(this);
        ctlRateBar.setMax(100);
        ctlRateBar.setProgress(100);
        ctlRateBar.setPadding(0, dp(6), 0, dp(6));
        c2.addView(ctlRateBar);
        LinearLayout rRt = new LinearLayout(this);
        rRt.setOrientation(LinearLayout.HORIZONTAL);
        rRt.setGravity(Gravity.CENTER_VERTICAL);
        ctlRateVal = text("（全局）", 13, C_ACCENT, true);
        ctlRateVal.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        rRt.addView(ctlRateVal);
        ctlRateClear = btn("跟随全局", 0, 0, "ctl-rateclear");
        LinearLayout.LayoutParams cl = new LinearLayout.LayoutParams(dp(100), dp(40));
        ctlRateClear.setLayoutParams(cl);
        rRt.addView(ctlRateClear);
        c2.addView(rRt);
        c2.addView(text("没人叫她时主动接话的概率：0% 几乎不插话，100% 每条都想插一句。", 12, C_SUB, false));

        // 戳一戳回复概率：11 个挡位按钮（概率点不准，用挡位比滑动条好使）
        TextView lPk = text("戳一戳回复概率", 15, C_TXT, false);
        lPk.setPadding(0, dp(16), 0, dp(8));
        c2.addView(lPk);
        ctlPokeBtns = new Button[CTL_POKE_STEPS.length];
        Button[] rowPk = new Button[CTL_POKE_STEPS.length];
        int half = (CTL_POKE_STEPS.length + 1) / 2;      // 6 + 5 两行，手机才放得下
        for (int start = 0; start < CTL_POKE_STEPS.length; start += half) {
            LinearLayout rr = new LinearLayout(this);
            rr.setOrientation(LinearLayout.HORIZONTAL);
            int cnt = Math.min(half, CTL_POKE_STEPS.length - start);
            for (int i = 0; i < cnt; i++) {
                final int idx = start + i;
                final double v = CTL_POKE_STEPS[idx];
                String label = (int) Math.round(v * 100) + "%";
                Button b = btn(label, 0, 0, "ctl-poke");
                LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(44), 1f);
                p.setMargins(dp(3), 0, dp(3), dp(6));
                b.setLayoutParams(p);
                b.setTag(Double.valueOf(v));
                b.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View vv) { ctlPoke = v; ctlPaint(); }
                });
                ctlPokeBtns[idx] = b;
                rowPk[idx] = b;
                rr.addView(b);
            }
            // 补齐空位，别让最后一行把按钮拉宽
            for (int i = cnt; i < half; i++) {
                View gapV = new View(this);
                LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(44), 1f);
                p.setMargins(dp(3), 0, dp(3), dp(6));
                gapV.setLayoutParams(p);
                rr.addView(gapV);
            }
            c2.addView(rr);
        }
        ctlPokeVal = text("100%", 13, C_ACCENT, true);
        ctlPokeVal.setPadding(0, dp(2), 0, 0);
        c2.addView(ctlPokeVal);
        c2.addView(text("被戳时回应的概率：100% 每次都回，10% 十次回一次，1% 很少理。", 12, C_SUB, false));

        // 默默发表情：看群友聊天时有概率只丢一张图、不说话（11 个挡位，0% = 关掉）
        TextView lAm = text("默默发表情（只发图不说话）", 15, C_TXT, false);
        lAm.setPadding(0, dp(16), 0, dp(8));
        c2.addView(lAm);
        ctlAmbientBtns = new Button[CTL_AMBIENT_STEPS.length];
        int halfAm = (CTL_AMBIENT_STEPS.length + 1) / 2;   // 6 + 5 两行
        for (int start = 0; start < CTL_AMBIENT_STEPS.length; start += halfAm) {
            LinearLayout rr = new LinearLayout(this);
            rr.setOrientation(LinearLayout.HORIZONTAL);
            int cnt = Math.min(halfAm, CTL_AMBIENT_STEPS.length - start);
            for (int i = 0; i < cnt; i++) {
                final int idx = start + i;
                final double v = CTL_AMBIENT_STEPS[idx];
                String label = (int) Math.round(v * 100) + "%";
                Button b = btn(label, 0, 0, "ctl-ambient");
                LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(44), 1f);
                p.setMargins(dp(3), 0, dp(3), dp(6));
                b.setLayoutParams(p);
                b.setTag(Double.valueOf(v));
                b.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View vv) { ctlAmbient = v; ctlPaint(); }
                });
                ctlAmbientBtns[idx] = b;
                rr.addView(b);
            }
            for (int i = cnt; i < halfAm; i++) {
                View gapV = new View(this);
                LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(44), 1f);
                p.setMargins(dp(3), 0, dp(3), dp(6));
                gapV.setLayoutParams(p);
                rr.addView(gapV);
            }
            c2.addView(rr);
        }
        ctlAmbientVal = text("3%", 13, C_ACCENT, true);
        ctlAmbientVal.setPadding(0, dp(2), 0, dp(0));
        c2.addView(ctlAmbientVal);
        ctlAmbientFollow = btn("用默认 3%", 0, 0, "ctl-ambientdef");
        LinearLayout.LayoutParams aflp = new LinearLayout.LayoutParams(dp(130), dp(40));
        aflp.setMargins(0, dp(6), 0, dp(4));
        ctlAmbientFollow.setLayoutParams(aflp);
        c2.addView(ctlAmbientFollow);
        c2.addView(text("她在群里看别人聊天时，偶尔只丢一张表情包、一个字都不说。"
                + "这是「每来一条群消息」的概率，0% = 这个群关掉。", 12, C_SUB, false));
        c2.addView(text("另有硬限制：同一个群 10 分钟最多一次、一小时 4 次；她刚说完话的几秒内不再甩图；"
                + "静默时段整段时间都不发。", 12, C_SUB, false));


        // 句式长度
        TextView lLen = text("句式长度", 15, C_TXT, false);
        lLen.setPadding(0, dp(14), 0, dp(6));
        c2.addView(lLen);
        LinearLayout rLen = new LinearLayout(this);
        rLen.setOrientation(LinearLayout.HORIZONTAL);
        ctlLen14 = btn("14", 0, 0, "ctl-len14");
        ctlLen26 = btn("26", 0, 0, "ctl-len26");
        ctlLen40 = btn("40", 0, 0, "ctl-len40");
        Button[] lens = {ctlLen14, ctlLen26, ctlLen40};
        for (int i = 0; i < lens.length; i++) {
            LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(46), 1f);
            p.setMargins(dp(3), 0, dp(3), 0);
            lens[i].setLayoutParams(p);
            rLen.addView(lens[i]);
        }
        c2.addView(rLen);
        ctlLenVal = text("当前：26 字", 12, C_SUB, false);
        ctlLenVal.setPadding(0, dp(8), 0, 0);
        c2.addView(ctlLenVal);
        foldExisting(c2, true);
        page.addView(c2);

        // 保存
        LinearLayout c3 = card();
        c3.addView(buttonRow(
                btn("保存到这个群", C_ACCENT, 0xFFFFFFFF, "ctl-save"),
                btn("重新读取", 0, 0, "ctl-reload")));
        ctlStatus = text("选个群先载入，再改。", 12, C_SUB, false);
        ctlStatus.setPadding(0, dp(8), 0, 0);
        c3.addView(ctlStatus);
        page.addView(c3);

        // ---- 她的声音（全局设置，不属于某个群，点了立刻生效）----
        LinearLayout c4 = card();
        c4.addView(text("她的声音", 16, C_TXT, true));
        TextView vTip = text("说话的音色。点一下立刻生效，不用重启。", 12, C_SUB, false);
        vTip.setPadding(0, dp(6), 0, dp(10));
        c4.addView(vTip);
        LinearLayout rV = new LinearLayout(this);
        rV.setOrientation(LinearLayout.HORIZONTAL);
        ctlVoiceOrig = btn("原音色", 0, 0, "ctl-voice-orig");
        ctlVoiceSoft = btn("软音色", 0, 0, "ctl-voice-soft");
        Button[] vs = {ctlVoiceOrig, ctlVoiceSoft};
        for (int i = 0; i < vs.length; i++) {
            LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(46), 1f);
            p.setMargins(dp(3), 0, dp(3), 0);
            vs[i].setLayoutParams(p);
            rV.addView(vs[i]);
        }
        c4.addView(rV);
        ctlVoiceVal = text("当前：原音色", 12, C_ACCENT, true);
        ctlVoiceVal.setPadding(0, dp(8), 0, 0);
        c4.addView(ctlVoiceVal);
        TextView vDesc = text("软音色念得更慢、更绵；不耐烦和撒娇仍是各自专属音色，保留情绪反差。",
                12, C_SUB, false);
        vDesc.setPadding(0, dp(6), 0, 0);
        c4.addView(vDesc);
        foldExisting(c4, false);
        page.addView(c4);

        // ---- 沉默名单（全局：拉黑别的机器人）----
        LinearLayout c5 = card();
        c5.addView(text("沉默名单（不理会的机器人）", 16, C_TXT, true));
        TextView blTip = text("填别的机器人的 QQ 号，这些号发的消息她一律当没看见（不记录、不学记忆、不回复）。全局生效，不分群。",
                12, C_SUB, false);
        blTip.setPadding(0, dp(6), 0, dp(10));
        c5.addView(blTip);

        blInput = new EditText(this);
        blInput.setHint("机器人 QQ 号");
        blInput.setHintTextColor(C_SUB);
        blInput.setTextColor(C_TXT);
        blInput.setTextSize(14);
        blInput.setSingleLine(true);
        blInput.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        blInput.setBackground(roundBg(C_BTN, C_LINE, 12));
        blInput.setPadding(dp(12), dp(10), dp(12), dp(10));
        blInput.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        c5.addView(blInput);

        blNote = new EditText(this);
        blNote.setHint("备注（可不填）");
        blNote.setHintTextColor(C_SUB);
        blNote.setTextColor(C_TXT);
        blNote.setTextSize(14);
        blNote.setSingleLine(true);
        blNote.setBackground(roundBg(C_BTN, C_LINE, 12));
        blNote.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams nl = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        nl.setMargins(0, dp(8), 0, 0);
        blNote.setLayoutParams(nl);
        c5.addView(blNote);

        LinearLayout blRow = new LinearLayout(this);
        blRow.setOrientation(LinearLayout.HORIZONTAL);
        blRow.setPadding(0, dp(10), 0, 0);
        Button blAdd = btn("拉黑", C_ACCENT, 0xFFFFFFFF, "bl-add");
        blToggle = btn("展开名单 ▾", 0, 0, "bl-toggle");
        Button[] bb = {blAdd, blToggle};
        for (int i = 0; i < bb.length; i++) {
            LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, dp(48), 1f);
            p.setMargins(dp(3), 0, dp(3), 0);
            bb[i].setLayoutParams(p);
            blRow.addView(bb[i]);
        }
        c5.addView(blRow);

        blHint = text("", 12, C_SUB, false);
        blHint.setPadding(0, dp(8), 0, 0);
        c5.addView(blHint);

        blList = new LinearLayout(this);
        blList.setOrientation(LinearLayout.VERTICAL);
        blList.setBackground(roundBg(C_BG, C_LINE, 10));
        blList.setPadding(dp(10), dp(8), dp(10), dp(8));
        LinearLayout.LayoutParams llp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        llp.setMargins(0, dp(10), 0, 0);
        blList.setLayoutParams(llp);
        blList.setVisibility(View.GONE);
        c5.addView(blList);
        foldExisting(c5, false);
        page.addView(c5);

        // 事件
        ctlVoiceOrig.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlSetVoice("diona"); }
        });
        ctlVoiceSoft.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlSetVoice("diona_soft"); }
        });
        ctlAtBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlOnlyAt = !ctlOnlyAt; ctlPaint(); }
        });
        ctlSpeedBar.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar s, int p, boolean fromUser) {
                ctlSpeed = (p + 30) / 100.0;
                ctlPaint();
            }
            public void onStartTrackingTouch(SeekBar s) { }
            public void onStopTrackingTouch(SeekBar s) { }
        });
        ctlRateBar.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar s, int p, boolean fromUser) {
                ctlRate = p / 100.0;
                if (fromUser) ctlPaint();      // 手拖就脱离「跟全局」，让「跟全局」按钮状态跟着变
                else ctlRateVal.setText(p + "%");
            }
            public void onStartTrackingTouch(SeekBar s) { }
            public void onStopTrackingTouch(SeekBar s) { }
        });
        ctlRateClear.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlRate = -1.0; ctlPaint(); }
        });
        ctlLen14.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlLen = 14; ctlPaint(); }
        });
        ctlLen26.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlLen = 26; ctlPaint(); }
        });
        ctlLen40.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { ctlLen = 40; ctlPaint(); }
        });
        // 输入群号后「载入」
        Button loadGid = findBtn(c1, "载入这个群号");
        if (loadGid != null) {
            loadGid.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    String g = "";
                    for (char ch : gidInput.getText().toString().toCharArray())
                        if (Character.isDigit(ch)) g += ch;
                    if (g.length() < 5) { toast("群号看着不对"); return; }
                    ctlChat = "群 " + g;
                    ctlStatus.setText("读取 群 " + g + " …");
                    ctlLoadRules(ctlChat);
                }
            });
        }
        ctlLoadVoice();          // 音色是全局设置，进页面就拉一次
        blLoad();                // 沉默名单也是全局
        page.addView(buildMemeCard());   // 梗库（纯文字）
        page.addView(buildApiCard());   // API 渠道（可命名 / 热切换）
        page.addView(buildLibCard());   // 图库（折叠，展开时才去拉数据）
        page.addView(buildDrawTplCard());   // 画图模板（2026-11-11 手机端补：改提示词/启停）
        page.addView(buildDrawLibCard());   // 画图图库（2026-11-11 手机端补：缩略图/收藏/删除）
        psLoadCard();                    // 角色人格（灰度中）：进页面就拉一次
        page.addView(psCard);
        return page;
    }

    // ============================================================ 角色人格（多套人设，灰度中）
    // 一套人设 = personas/<id>/persona_prompt.txt + wordbank.json。
    // 灰度：只有 群 1000000006 / 超管私聊 会换成选中的那个，其余群永远是默认嘴。
    LinearLayout psCard, psList;
    TextView psStatus, psMeta;
    EditText psIdIn, psNameIn, psPrompt;
    String psSel = "";

    void psLoadCard() {
        psCard = card();
        psCard.addView(text("角色人格（多套人设，灰度中）", 16, C_TXT, true));
        TextView tip = text("一套人设 = 一份人设正文 + 它自己的一套文案（指令回复、好感度、投喂、戳一戳、接梗…）。\n"
                + "灰度中：只有 群 1000000006 和超管(1000000002)的私聊会用选中的这一个人格，其他群完全不受影响。",
                12, C_SUB, false);
        tip.setPadding(0, dp(6), 0, dp(8));
        psCard.addView(tip);

        psStatus = text("读取中…", 13, C_ACCENT, true);
        psCard.addView(psStatus);

        psList = new LinearLayout(this);
        psList.setOrientation(LinearLayout.VERTICAL);
        psList.setPadding(0, dp(6), 0, 0);
        psCard.addView(psList);

        LinearLayout rowNew = new LinearLayout(this);
        rowNew.setOrientation(LinearLayout.HORIZONTAL);
        rowNew.setPadding(0, dp(10), 0, 0);
        psIdIn = psInput("新 id（字母/数字）");
        psNameIn = psInput("显示名");
        Button bNew = btn("新建（空模板）", C_ACCENT, 0xFFFFFFFF, "ps-new");
        LinearLayout.LayoutParams nlp = new LinearLayout.LayoutParams(0, dp(44), 1.1f);
        nlp.setMargins(dp(3), 0, dp(3), 0);
        bNew.setLayoutParams(nlp);
        LinearLayout.LayoutParams ilp1 = new LinearLayout.LayoutParams(0, dp(44), 1f);
        ilp1.setMargins(dp(3), 0, dp(3), 0);
        psIdIn.setLayoutParams(ilp1);
        LinearLayout.LayoutParams ilp2 = new LinearLayout.LayoutParams(0, dp(44), 0.9f);
        ilp2.setMargins(dp(3), 0, dp(3), 0);
        psNameIn.setLayoutParams(ilp2);
        rowNew.addView(psIdIn);
        rowNew.addView(psNameIn);
        rowNew.addView(bNew);
        psCard.addView(rowNew);

        psMeta = text("人设正文（写在这个人格自己的 persona_prompt.txt 里）", 13, C_TXT, false);
        psMeta.setPadding(0, dp(14), 0, dp(6));
        psCard.addView(psMeta);

        psPrompt = new EditText(this);
        psPrompt.setHint("（这个角色还没写正文）");
        psPrompt.setHintTextColor(C_SUB);
        psPrompt.setTextColor(C_TXT);
        psPrompt.setTextSize(13);
        psPrompt.setGravity(Gravity.TOP);
        psPrompt.setBackground(roundBg(C_BTN, C_LINE, 12));
        psPrompt.setPadding(dp(10), dp(8), dp(10), dp(8));
        psPrompt.setMinLines(6);
        psPrompt.setMaxLines(12);
        psPrompt.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(180)));
        psCard.addView(psPrompt);

        LinearLayout rowP = new LinearLayout(this);
        rowP.setOrientation(LinearLayout.HORIZONTAL);
        rowP.setPadding(0, dp(8), 0, 0);
        Button bSave = btn("保存正文", C_ACCENT, 0xFFFFFFFF, "ps-save");
        Button bRead = btn("读取选中的", 0, 0, "ps-read");
        LinearLayout.LayoutParams plp = new LinearLayout.LayoutParams(0, dp(46), 1f);
        plp.setMargins(dp(3), 0, dp(3), 0);
        bSave.setLayoutParams(plp);
        bRead.setLayoutParams(plp);
        rowP.addView(bSave);
        rowP.addView(bRead);
        psCard.addView(rowP);
        foldExisting(psCard, true);
        psLoad();
    }

    EditText psInput(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setHintTextColor(C_SUB);
        e.setTextColor(C_TXT);
        e.setTextSize(13);
        e.setSingleLine(true);
        e.setBackground(roundBg(C_BTN, C_LINE, 12));
        e.setPadding(dp(10), 0, dp(10), 0);
        return e;
    }

    void psLoad() {
        psStatus.setText("读取中…");
        getJson("/api/personas", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { psStatus.setText("拉不到人格列表：" + err); return; }
                psFill(o);
            }
        });
    }

    void psFill(JSONObject o) {
        psList.removeAllViews();
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        String active = o == null ? "" : o.optString("active");
        psStatus.setText("人格 " + (arr == null ? 0 : arr.length()) + " 个 · 选中：" + active);
        for (int i = 0; arr != null && i < arr.length(); i++) {
            final JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            final String id = it.optString("id");
            String name = it.optString("name");
            int have = it.optInt("have"), total = it.optInt("total");
            int plen = it.optInt("prompt_len");
            boolean isActive = id.equals(active);

            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(0, dp(8), 0, dp(8));
            row.setBackground(roundBg(isActive ? C_ACCENT_D : 0, 0, 10));

            String label = (isActive ? "● " : "○ ") + name + " (" + id + ")\n文案 " + have + "/" + total
                    + " · 人设 " + plen + " 字" + (have < total ? " · ⚠ 缺 " + (total - have) + " 项" : "");
            TextView tv = text(label, 12, C_TXT, isActive);
            LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
            tlp.setMargins(dp(6), 0, dp(6), 0);
            tv.setLayoutParams(tlp);
            row.addView(tv);

            Button bUse = btn(isActive ? "在用" : "切到", 0, 0, "ps-use");
            LinearLayout.LayoutParams ulp = new LinearLayout.LayoutParams(dp(62), dp(40));
            bUse.setLayoutParams(ulp);
            bUse.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { psUse(id); }
            });
            row.addView(bUse);

            Button bRead = btn("读", 0, 0, "ps-read");
            LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(dp(48), dp(40));
            bRead.setLayoutParams(rlp);
            bRead.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { psRead(id); }
            });
            row.addView(bRead);

            if (!id.equals("feiyu") && !isActive) {
                Button bDel = btn("删", 0, 0, "ps-del");
                LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(48), dp(40));
                bDel.setLayoutParams(dlp);
                bDel.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) { psDel(id); }
                });
                row.addView(bDel);
            }
            psList.addView(row);
        }
    }

    void psPost(String path, JSONObject body, final String okText) {
        postJsonBody(path, body, new JsonCb() {
            public void on(JSONObject o, String err) {
                boolean ok = err == null && o != null && o.optBoolean("ok");
                psStatus.setText(ok ? okText : ("失败：" + (err != null ? err
                        : (o == null ? "?" : o.optString("msg")))));
                if (ok) psLoad();
            }
        });
    }

    void psUse(final String id) {
        psStatus.setText("切换中…");
        JSONObject b = new JSONObject();
        try { b.put("id", id); } catch (Exception e) { }
        psPost("/api/personas/use", b, "已切到 " + id + "（灰度会话生效）");
    }

    void psDel(final String id) {
        JSONObject b = new JSONObject();
        try { b.put("id", id); } catch (Exception e) { }
        psPost("/api/personas/del", b, "已删除 " + id);
    }

    void psNew() {
        String id = psIdIn.getText().toString().trim();
        String nm = psNameIn.getText().toString().trim();
        if (id.length() == 0) { psStatus.setText("先填 id"); return; }
        JSONObject b = new JSONObject();
        try {
            b.put("id", id);
            b.put("name", nm.length() > 0 ? nm : id);
        } catch (Exception e) { }
        psPost("/api/personas/new", b, "已新建 " + id);
        psIdIn.setText("");
        psNameIn.setText("");
    }

    void psRead(final String id) {
        psSel = id;
        psMeta.setText("读取 " + id + " 的人设正文…");
        getJson("/api/personas/prompt?id=" + enc(id), new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { psMeta.setText("读取失败：" + err); return; }
                String t = o == null ? "" : o.optString("text");
                psPrompt.setText(t);
                psMeta.setText("编辑中：" + id + "（" + t.length() + " 字）");
            }
        });
    }

    void psSave() {
        if (psSel.length() == 0) { psStatus.setText("先在列表里点「读」选一个人格"); return; }
        psStatus.setText("保存中…");
        JSONObject b = new JSONObject();
        try {
            b.put("id", psSel);
            b.put("text", psPrompt.getText().toString());
        } catch (Exception e) { }
        psPost("/api/personas/prompt", b, "已保存 " + psSel + " 的人设正文");
    }



    // ============================================================ 梗库
    TextView mkStatus;
    LinearLayout mkList;
    EditText mkNames, mkDesc, mkJie;
    String mkStickerB64 = "";
    TextView mkStickerHint;
    android.widget.ImageView mkPreview;
    static final int REQ_PICK_MK = 0x7050;

    LinearLayout buildMemeCard() {
        LinearLayout c = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text("梗库（名字;名字 | 介绍）", 16, C_TXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        head.addView(t);
        final TextView arrow = text("展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        c.addView(head);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setVisibility(View.GONE);
        c.addView(body);
        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (show) mkLoad();
            }
        });

        TextView tip = text("纯文字。名字可用分号写多个（如 doro;桃乐丝）。\n"
                + "· 随口提到 → 只回「接梗话」（不念介绍）\n"
                + "· 真的在问（这是什么梗/啥意思） → 才查介绍 + 联网解释", 12, C_SUB, false);
        tip.setPadding(0, dp(6), 0, dp(8));
        body.addView(tip);

        mkStatus = text("读取中…", 13, C_TXT, true);
        body.addView(mkStatus);

        mkList = new LinearLayout(this);
        mkList.setOrientation(LinearLayout.VERTICAL);
        mkList.setPadding(0, dp(6), 0, dp(4));
        body.addView(mkList);

        mkNames = plInput("梗名字，多个用 ; 隔开");
        body.addView(mkNames);
        mkDesc = plInput("介绍（她照这个说）");
        mkDesc.setSingleLine(false);
        mkDesc.setMinLines(3);
        mkDesc.setGravity(Gravity.TOP);
        body.addView(mkDesc);

        body.addView(text("接梗话（随口提一句时回这个，多个用 ; 分隔；空=默认池）", 11, C_SUB, false));
        mkJie = plInput("如：哦这个啊;哈？又来");
        body.addView(mkJie);

        LinearLayout imgRow = new LinearLayout(this);
        imgRow.setOrientation(LinearLayout.HORIZONTAL);
        imgRow.setGravity(Gravity.CENTER_VERTICAL);
        Button pick = btn("选图存梗包", 0, C_TXT, "");
        LinearLayout.LayoutParams plp = new LinearLayout.LayoutParams(dp(118), dp(40));
        plp.setMargins(0, dp(4), dp(8), 0);
        pick.setLayoutParams(plp);
        pick.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { mkPickSticker(); }
        });
        imgRow.addView(pick);
        mkStickerHint = text("没选图", 11, C_SUB, false);
        imgRow.addView(mkStickerHint);
        body.addView(imgRow);

        mkPreview = new android.widget.ImageView(this);
        LinearLayout.LayoutParams pvp = new LinearLayout.LayoutParams(dp(84), dp(84));
        pvp.setMargins(0, 0, 0, dp(6));
        mkPreview.setLayoutParams(pvp);
        mkPreview.setScaleType(android.widget.ImageView.ScaleType.FIT_CENTER);
        mkPreview.setVisibility(View.GONE);
        body.addView(mkPreview);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        Button save = btn("保存梗", C_ACCENT, 0xFFFFFFFF, "");
        save.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { mkSave(); }
        });
        Button rf = btn("刷新", 0, C_TXT, "");
        rf.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { mkLoad(); }
        });
        LinearLayout.LayoutParams lp1 = new LinearLayout.LayoutParams(0, dp(46), 1);
        lp1.setMargins(0, dp(6), dp(8), 0);
        save.setLayoutParams(lp1);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(dp(80), dp(46));
        lp2.setMargins(0, dp(6), 0, 0);
        rf.setLayoutParams(lp2);
        row.addView(save);
        row.addView(rf);
        body.addView(row);
        return c;
    }

    void mkLoad() {
        if (mkStatus == null) return;
        mkStatus.setText("读取中…");
        getJson("/api/memes", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (mkStatus != null) mkStatus.setText("读取失败：" + err);
                    return;
                }
                mkPaint(o);
            }
        });
    }

    void mkPaint(JSONObject o) {
        if (mkList == null) return;
        int cnt = o == null ? 0 : o.optInt("count");
        if (mkStatus != null) mkStatus.setText("梗库共 " + cnt + " 条");
        mkList.removeAllViews();
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        if (arr == null || arr.length() == 0) {
            mkList.addView(text("还空着：下面填个名字和介绍就有一条了。", 12, C_SUB, false));
            return;
        }
        for (int i = 0; i < arr.length(); i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            if (plFilterTag.length() > 0) {
                boolean hitT = false;
                JSONArray ft = it.optJSONArray("tags");
                if (ft != null) {
                    for (int q = 0; q < ft.length(); q++) {
                        if (plFilterTag.equals(ft.optString(q))) { hitT = true; break; }
                    }
                }
                if (!hitT) continue;
            }
            final String id = it.optString("id");
            String desc = it.optString("desc");
            int hits = it.optInt("hits");
            JSONArray nv = it.optJSONArray("names");
            StringBuilder sb = new StringBuilder();
            if (nv != null) {
                for (int k = 0; k < nv.length(); k++) {
                    if (k > 0) sb.append("、");
                    sb.append(nv.optString(k));
                }
            }
            final String nm = sb.toString();
            LinearLayout r = new LinearLayout(this);
            r.setOrientation(LinearLayout.HORIZONTAL);
            r.setGravity(Gravity.CENTER_VERTICAL);
            r.setPadding(0, dp(6), 0, dp(6));
            String jie = it.optString("jie");
            boolean hasStk = it.optBoolean("has_sticker");
            String line = "· " + nm + "   命中 " + hits + (hasStk ? "  🖼" : "") + "\n   "
                    + (desc.length() > 40 ? desc.substring(0, 40) + "…" : desc)
                    + (jie.length() > 0 ? "\n   ▶接梗：" + jie : "");
            TextView tv = text(line, 12, C_TXT, false);
            tv.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            r.addView(tv);
            final String fdesc = desc;
            final String fjie = jie;
            if (hasStk) {
                android.widget.ImageView th = new android.widget.ImageView(this);
                LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(dp(48), dp(48));
                tlp.setMargins(dp(4), 0, 0, 0);
                th.setLayoutParams(tlp);
                th.setScaleType(android.widget.ImageView.ScaleType.FIT_CENTER);
                r.addView(th);
                mkLoadThumb(id, th);
            }
            Button ed = btn("改", 0, C_TXT, "");
            LinearLayout.LayoutParams elp = new LinearLayout.LayoutParams(dp(44), dp(38));
            elp.setMargins(dp(4), 0, 0, 0);
            ed.setLayoutParams(elp);
            ed.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { mkEdit(id, nm, fdesc, fjie); }
            });
            r.addView(ed);
            Button del = btn("删", 0, C_BAD, "");
            LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(44), dp(38));
            dlp.setMargins(dp(4), 0, 0, 0);
            del.setLayoutParams(dlp);
            del.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { mkDel(id, nm); }
            });
            r.addView(del);
            mkList.addView(r);
        }
    }

    void mkEdit(String id, String names, String desc, String jie) {
        if (mkNames != null) mkNames.setText(names.replace("、", ";"));
        if (mkDesc != null) mkDesc.setText(desc);
        if (mkJie != null) mkJie.setText(jie == null ? "" : jie);
        if (mkStatus != null) mkStatus.setText("改完点「保存梗」（同名会更新那一条）");
    }


    void mkLoadThumb(final String id, final android.widget.ImageView iv) {
        new Thread(new Runnable() {
            public void run() {
                final Bitmap bm = httpBitmap("/api/memes/sticker?id=" + id);
                if (bm == null) return;
                ui.post(new Runnable() {
                    public void run() { plBmp.put(id, bm); iv.setImageBitmap(bm); }
                });
            }
        }).start();
    }

    void mkShowLocalPreview(byte[] raw) {
        try {
            final Bitmap bm = BitmapFactory.decodeByteArray(raw, 0, raw.length);
            if (bm != null && mkPreview != null) {
                mkPreview.setImageBitmap(bm);
                mkPreview.setVisibility(View.VISIBLE);
            }
        } catch (Throwable t) { }
    }

    void mkPickSticker() {
        Intent i = new Intent(Intent.ACTION_GET_CONTENT);
        i.setType("image/*");
        try {
            startActivityForResult(Intent.createChooser(i, "选一张图当梗包"), REQ_PICK_MK);
        } catch (Exception e) {
            toast("打不开相册：" + e.getMessage());
        }
    }

    void mkOnPick(int req, int result, Intent data) {
        if (req != REQ_PICK_MK) return;
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        try {
            java.io.InputStream in = getContentResolver().openInputStream(data.getData());
            byte[] raw = readAllBytes(in);
            if (in != null) in.close();
            if (raw == null || raw.length == 0) { toast("读不到图"); return; }
            if (raw.length > 3 * 1024 * 1024) { toast("图太大（>3MB）"); return; }
            byte[] small = shrinkJpeg(raw, 320);
            if (small != null) raw = small;
            mkStickerB64 = android.util.Base64.encodeToString(raw, android.util.Base64.NO_WRAP);
            mkShowLocalPreview(raw);
            if (mkStickerHint != null) mkStickerHint.setText("已选（保存即生效）");
        } catch (Exception e) {
            toast("读图失败：" + e.getMessage());
        }
    }

    void mkSave() {
        String nm = mkNames == null ? "" : mkNames.getText().toString().trim();
        String ds = mkDesc == null ? "" : mkDesc.getText().toString().trim();
        if (nm.length() == 0 || ds.length() == 0) {
            toast("名字和介绍都要填");
            return;
        }
        String jie = mkJie == null ? "" : mkJie.getText().toString().trim();
        JSONObject body = new JSONObject();
        try {
            body.put("names", nm);
            body.put("desc", ds);
            if (jie.length() > 0) body.put("jie", jie);
            if (mkStickerB64 != null && mkStickerB64.length() > 0) body.put("image_b64", mkStickerB64);
        } catch (Exception e) { }
        if (mkStatus != null) mkStatus.setText("保存中…");
        postJsonBody("/api/memes/add", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (mkStatus != null) mkStatus.setText("保存失败：" + err);
                    return;
                }
                if (o != null && !o.optBoolean("ok")) {
                    if (mkStatus != null) mkStatus.setText(o.optString("msg", "保存失败"));
                    return;
                }
                if (mkNames != null) mkNames.setText("");   // 存完清空，方便接着加
                if (mkDesc != null) mkDesc.setText("");
                if (mkJie != null) mkJie.setText("");
                mkStickerB64 = "";
                if (mkPreview != null) { mkPreview.setImageBitmap(null); mkPreview.setVisibility(View.GONE); }
                if (mkStickerHint != null) mkStickerHint.setText("没选图");
                mkPaint(o);
            }
        });
    }

    void mkDel(String id, String names) {
        JSONObject body = new JSONObject();
        try { body.put("id", id); } catch (Exception e) { }
        postJsonBody("/api/memes/del", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (mkStatus != null) mkStatus.setText("删除失败：" + err);
                    return;
                }
                mkPaint(o);
            }
        });
    }

    // ============================================================ API 渠道
    TextView apiStatus, apiLast;
    LinearLayout apiList;
    EditText apName, apBase, apModel, apKey;

    LinearLayout buildApiCard() {
        LinearLayout c = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text("API 渠道（可命名 / 热切换）", 16, C_TXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        head.addView(t);
        final TextView arrow = text("展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        c.addView(head);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setVisibility(View.GONE);
        c.addView(body);
        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (show) apiLoad();
            }
        });

        TextView tip = text("多个渠道各自起名，点「用这个」立刻切换（不用重启）。"
                + "只想换 key：名字 + Key 填上就行，地址/模型留空 = 沿用 .env。", 12, C_SUB, false);
        tip.setPadding(0, dp(6), 0, dp(8));
        body.addView(tip);

        apiStatus = text("读取中…", 13, C_TXT, true);
        body.addView(apiStatus);
        apiLast = text("", 11, C_SUB, false);
        apiLast.setPadding(0, dp(4), 0, 0);
        body.addView(apiLast);

        apiList = new LinearLayout(this);
        apiList.setOrientation(LinearLayout.VERTICAL);
        apiList.setPadding(0, dp(6), 0, dp(4));
        body.addView(apiList);

        apName = plInput("名字，如 主渠道 / 备1");
        body.addView(apName);
        apBase = plInput("地址，如 https://xxx/v1/chat/completions");
        body.addView(apBase);
        apModel = plInput("模型名，如 deepseek-v4-flash-0731");
        body.addView(apModel);
        apKey = plInput("Key（留空=不改）");
        body.addView(apKey);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        Button save = btn("保存渠道", C_ACCENT, 0xFFFFFFFF, "");
        save.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { apiSave(); }
        });
        Button test = btn("测试", 0, C_TXT, "");
        test.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { apiTest(); }
        });
        Button rf = btn("刷新", 0, C_TXT, "");
        rf.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { apiLoad(); }
        });
        LinearLayout.LayoutParams lp1 = new LinearLayout.LayoutParams(0, dp(46), 1);
        lp1.setMargins(0, dp(6), dp(8), 0);
        save.setLayoutParams(lp1);
        LinearLayout.LayoutParams lpT = new LinearLayout.LayoutParams(dp(72), dp(46));
        lpT.setMargins(0, dp(6), dp(8), 0);
        test.setLayoutParams(lpT);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(dp(72), dp(46));
        lp2.setMargins(0, dp(6), 0, 0);
        rf.setLayoutParams(lp2);
        row.addView(save);
        row.addView(test);
        row.addView(rf);
        body.addView(row);
        return c;
    }

    void apiLoad() {
        if (apiStatus == null) return;
        apiStatus.setText("读取中…");
        getJson("/api/apis", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (apiStatus != null) apiStatus.setText("读取失败：" + err);
                    return;
                }
                apiPaint(o);
            }
        });
    }

    void apiPaint(JSONObject o) {
        if (apiList == null) return;
        String active = o == null ? "" : o.optString("active", "");
        if (apiStatus != null)
            apiStatus.setText(active.length() == 0 ? "当前：默认（.env）" : ("当前：「" + active + "」"));
        if (apiLast != null) {
            JSONObject last = o == null ? null : o.optJSONObject("last");
            if (last == null || last.optString("at").length() == 0) {
                apiLast.setText("上次调用：还没有记录（说句话就会记）");
                apiLast.setTextColor(C_SUB);
            } else {
                boolean lok = last.optBoolean("ok");
                apiLast.setText("上次调用：" + (lok ? "成功 200" : ("失败 HTTP " + last.optInt("status")))
                        + "（" + last.optString("channel") + "）" + last.optString("at"));
                apiLast.setTextColor(lok ? C_SUB : C_BAD);
            }
        }
        apiList.removeAllViews();
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        if (arr == null || arr.length() == 0) {
            apiList.addView(text("还没有渠道：填下面的表单保存一个（第一个会自动设为当前）。", 12, C_SUB, false));
            return;
        }
        for (int i = 0; i < arr.length(); i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            final String nm = it.optString("name");
            String baseUrl = it.optString("base");
            String model = it.optString("model");
            String mask = it.optString("key_mask");
            boolean isActive = it.optBoolean("active");
            LinearLayout r = new LinearLayout(this);
            r.setOrientation(LinearLayout.HORIZONTAL);
            r.setGravity(Gravity.CENTER_VERTICAL);
            r.setPadding(0, dp(6), 0, dp(6));
            String line = (isActive ? "● " : "· ") + nm + "\n   " + model
                    + (mask.length() > 0 ? "   key " + mask : "   key …");
            TextView tv = text(line, 12, isActive ? C_ACCENT : C_TXT, isActive);
            tv.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            r.addView(tv);
            if (!isActive) {
                Button use = btn("用", 0, C_TXT, "");
                LinearLayout.LayoutParams ulp = new LinearLayout.LayoutParams(dp(44), dp(38));
                ulp.setMargins(dp(4), 0, 0, 0);
                use.setLayoutParams(ulp);
                use.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) { apiUse(nm); }
                });
                r.addView(use);
            }
            Button ed = btn("改", 0, C_TXT, "");
            LinearLayout.LayoutParams elp = new LinearLayout.LayoutParams(dp(44), dp(38));
            elp.setMargins(dp(4), 0, 0, 0);
            ed.setLayoutParams(elp);
            final String fb = baseUrl, fm = model;
            ed.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { apiEdit(nm, fb, fm); }
            });
            r.addView(ed);
            // 2026-11-11：一键复制「名字 | 地址 | 模型 | key 掩码」到剪贴板（手机上照着抄太费劲）
            Button cp = btn("复", 0, C_TXT, "");
            LinearLayout.LayoutParams clp = new LinearLayout.LayoutParams(dp(44), dp(38));
            clp.setMargins(dp(4), 0, 0, 0);
            cp.setLayoutParams(clp);
            final String fbase = baseUrl, fmodel = model, fmask = mask;
            cp.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    String txt = nm + " | " + fbase + " | " + fmodel + " | " + fmask;
                    try {
                        android.content.ClipboardManager cm = (android.content.ClipboardManager)
                                getSystemService(CLIPBOARD_SERVICE);
                        cm.setPrimaryClip(android.content.ClipData.newPlainText("fish-api", txt));
                        toast("已复制：" + txt);
                    } catch (Throwable t) {
                        toast("复制失败：" + t.getMessage());
                    }
                }
            });
            r.addView(cp);
            Button del = btn("删", 0, C_BAD, "");
            LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(44), dp(38));
            dlp.setMargins(dp(4), 0, 0, 0);
            del.setLayoutParams(dlp);
            del.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { apiDel(nm); }
            });
            r.addView(del);
            apiList.addView(r);
        }
    }

    void apiEdit(String name, String baseUrl, String model) {
        if (apName != null) apName.setText(name);
        if (apBase != null) apBase.setText(baseUrl);
        if (apModel != null) apModel.setText(model);
        if (apKey != null) apKey.setText("");
        if (apiStatus != null) apiStatus.setText("改了「" + name + "」后点「保存渠道」");
    }

    // ============================================================ 画图模板（2026-11-11 手机端补）
    // 桌面/WPF 早就有「画图模板」页，手机端一直缺。插件是按 mtime 热读 data/draw_styles.json 的，
    // 所以这里保存完下一条 /画图 就生效，不用重启。
    TextView dtStatus;
    LinearLayout dtList;

    LinearLayout buildDrawTplCard() {
        LinearLayout c = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text("画图模板（热读 · 改完下一条生效）", 16, C_TXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        head.addView(t);
        final TextView arrow = text("展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        c.addView(head);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setVisibility(View.GONE);
        c.addView(body);
        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (show) dtLoad();
            }
        });

        dtStatus = text("读取中…", 13, C_TXT, true);
        body.addView(dtStatus);
        dtList = new LinearLayout(this);
        dtList.setOrientation(LinearLayout.VERTICAL);
        dtList.setPadding(0, dp(6), 0, 0);
        body.addView(dtList);

        Button rf = btn("刷新模板", 0, C_TXT, "");
        rf.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { dtLoad(); }
        });
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(44));
        lp.setMargins(0, dp(8), 0, 0);
        rf.setLayoutParams(lp);
        body.addView(rf);
        return c;
    }

    void dtLoad() {
        if (dtStatus == null) return;
        dtStatus.setText("读取中…");
        getJson("/api/draw_styles", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (dtStatus != null) dtStatus.setText("读取失败：" + err);
                    return;
                }
                dtPaint(o);
            }
        });
    }

    void dtPaint(JSONObject o) {
        if (dtList == null) return;
        dtList.removeAllViews();
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        int total = arr == null ? 0 : arr.length();
        if (dtStatus != null) dtStatus.setText("共 " + total + " 个模板（「改」编辑提示词，「停/启」控制生效）");
        if (arr == null) return;
        for (int i = 0; i < arr.length(); i++) {
            final JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            final String id = it.optString("id");
            final String nm2 = it.optString("name");
            final String kind = it.optString("kind");
            final String prompt = it.optString("prompt");
            final boolean enabled = it.optBoolean("enabled", true);
            LinearLayout r = new LinearLayout(this);
            r.setOrientation(LinearLayout.HORIZONTAL);
            r.setGravity(Gravity.CENTER_VERTICAL);
            r.setPadding(0, dp(4), 0, dp(4));
            TextView tv = text(nm2 + "（" + kind + "）" + prompt.length() + " 字"
                            + (enabled ? "" : " · 已停用"),
                    13, enabled ? C_TXT : C_SUB, false);
            tv.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            r.addView(tv);

            Button ed = btn("改", 0, C_TXT, "");
            LinearLayout.LayoutParams lpE = new LinearLayout.LayoutParams(dp(52), dp(38));
            lpE.setMargins(0, 0, dp(6), 0);
            ed.setLayoutParams(lpE);
            ed.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { dtEdit(id, nm2, kind, prompt, enabled); }
            });
            r.addView(ed);

            Button tg = btn(enabled ? "停" : "启", 0, enabled ? C_BAD : C_ACCENT, "");
            LinearLayout.LayoutParams lpT = new LinearLayout.LayoutParams(dp(52), dp(38));
            tg.setLayoutParams(lpT);
            tg.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { dtToggle(id, nm2, kind, prompt, !enabled); }
            });
            r.addView(tg);
            dtList.addView(r);
        }
    }

    void dtToggle(String id, String name, String kind, String prompt, boolean enable) {
        JSONObject b = new JSONObject();
        try {
            b.put("id", id);
            b.put("name", name);
            b.put("kind", kind);
            b.put("prompt", prompt);
            b.put("enabled", enable);
        } catch (Exception e) { }
        if (dtStatus != null) dtStatus.setText("保存中…");
        postJsonBody("/api/draw_styles/save", b, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { if (dtStatus != null) dtStatus.setText("保存失败：" + err); return; }
                boolean ok = o != null && o.optBoolean("ok");
                if (!ok && dtStatus != null) dtStatus.setText(o == null ? "保存失败" : o.optString("msg", "保存失败"));
                dtLoad();
            }
        });
    }

    void dtEdit(final String id, final String name, final String kind,
                String prompt, final boolean enabled) {
        final EditText e = new EditText(this);
        e.setText(prompt);
        e.setMinLines(8);
        e.setMaxLines(20);
        e.setTextSize(12);
        e.setGravity(Gravity.TOP | Gravity.START);
        ScrollView sv = new ScrollView(this);
        sv.addView(e);
        new AlertDialog.Builder(this)
                .setTitle("改「" + name + "」的提示词")
                .setView(sv)
                .setPositiveButton("保存", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        String txt = e.getText().toString();
                        if (txt.trim().length() == 0) { toast("提示词不能空"); return; }
                        dtToggle(id, name, kind, txt, enabled);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    // ============================================================ 画图图库（2026-11-11 手机端补）
    // 桌面/WPF 早就有；手机端一直缺。数据在 data/drawlib/，接口：
    //   GET /api/drawlib?limit&offset&q&fav=1   → {ok,total,items[]}
    //   GET /api/drawlib/thumb?id=  → 缩略图 JPEG（列表用这个，快）
    //   GET /api/drawlib/img?id=    → 原图
    //   POST /api/drawlib/edit {id,fav} / /api/drawlib/del {id}
    TextView dlStatus;
    LinearLayout dlGrid;
    EditText dlQ;
    boolean dlFavOnly = false;
    int dlOffset = 0;
    int dlCount = 0;                                   // 服务端报的总数
    final java.util.HashMap<String, LinearLayout> dlCells = new java.util.HashMap<String, LinearLayout>();
    final java.util.ArrayList<JSONObject> dlItems = new java.util.ArrayList<JSONObject>();
    int dlGen = 0;   // 每次发起请求 +1；回来的响应对不上就是过期的，直接丢（防重复追加）
    final java.util.HashMap<String, Bitmap> dlBmp = new java.util.HashMap<String, Bitmap>();

    LinearLayout buildDrawLibCard() {
        LinearLayout c = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text("画图图库（每次 /画图 的原图）", 16, C_TXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        head.addView(t);
        final TextView arrow = text("展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        c.addView(head);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setVisibility(View.GONE);
        c.addView(body);
        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (show) dlLoad(false);
            }
        });

        dlStatus = text("读取中…", 13, C_TXT, true);
        body.addView(dlStatus);

        dlQ = plInput("搜标题 / 提示词 / 标签（留空=全部）");
        body.addView(dlQ);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        Button go = btn("搜索", C_ACCENT, 0xFFFFFFFF, "");
        go.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { dlLoad(false); }
        });
        final Button fav = btn("只看收藏", 0, C_TXT, "");
        fav.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                dlFavOnly = !dlFavOnly;
                fav.setText(dlFavOnly ? "看全部" : "只看收藏");
                dlLoad(false);
            }
        });
        // 2026-11-11 用户：「还没有手动刷新的按钮」—— 去掉整页自动重拉之后，得给一个手动入口
        Button rf2 = btn("刷新", 0, C_TXT, "");
        rf2.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { dlBmp.clear(); dlLoad(false); }
        });
        LinearLayout.LayoutParams lp1 = new LinearLayout.LayoutParams(0, dp(44), 1);
        lp1.setMargins(0, dp(6), dp(8), 0);
        go.setLayoutParams(lp1);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(dp(110), dp(44));
        lp2.setMargins(0, dp(6), dp(8), 0);
        fav.setLayoutParams(lp2);
        LinearLayout.LayoutParams lpR = new LinearLayout.LayoutParams(dp(72), dp(44));
        lpR.setMargins(0, dp(6), 0, 0);
        rf2.setLayoutParams(lpR);
        row.addView(go);
        row.addView(fav);
        row.addView(rf2);
        body.addView(row);

        dlGrid = new LinearLayout(this);
        dlGrid.setOrientation(LinearLayout.VERTICAL);
        dlGrid.setPadding(0, dp(8), 0, 0);
        body.addView(dlGrid);

        Button more = btn("加载更多", 0, C_TXT, "");
        more.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { dlLoad(true); }
        });
        LinearLayout.LayoutParams lp3 = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(44));
        lp3.setMargins(0, dp(8), 0, 0);
        more.setLayoutParams(lp3);
        body.addView(more);
        return c;
    }

    void dlLoad(final boolean append) {
        if (dlStatus == null || dlGrid == null) return;
        if (!append) { dlOffset = 0; dlItems.clear(); dlGrid.removeAllViews(); }
        dlStatus.setText("读取中…");
        String q = dlQ == null ? "" : dlQ.getText().toString().trim();
        String path = "/api/drawlib?limit=24&offset=" + dlOffset;
        if (q.length() > 0) {
            try { path += "&q=" + java.net.URLEncoder.encode(q, "UTF-8"); } catch (Exception e) { }
        }
        if (dlFavOnly) path += "&fav=1";
        final int gen = ++dlGen;          // 这次请求的编号
        getJson(path, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (gen != dlGen) return;   // 已经有更新的请求发出去了 → 这条是过期响应，丢掉
                if (err != null) {
                    if (dlStatus != null) dlStatus.setText("读取失败：" + err);
                    return;
                }
                dlPaint(o);
            }
        });
    }

    void dlPaint(JSONObject o) {
        if (dlGrid == null) return;
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        int total = o == null ? 0 : o.optInt("total");
        int n = arr == null ? 0 : arr.length();
        dlCount = total;
        if (n == 0) {
            if (dlItems.isEmpty())
                dlGrid.addView(text("没有符合条件的图。", 12, C_SUB, false));
            if (dlStatus != null)
                dlStatus.setText("共 " + dlCount + " 张（已显示 " + dlItems.size() + " 张）");
            return;
        }
        // 按 id 去重：加载更多时服务端窗口可能和已有条目重叠（并发/重复点），
        // 以前直接 append 会出现"只有 4 张却显示 8 张"的重复格子。
        java.util.HashSet<String> have = new java.util.HashSet<String>();
        for (int k = 0; k < dlItems.size(); k++) have.add(dlItems.get(k).optString("id"));
        int added = 0;
        for (int i = 0; i < n; i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            if (!have.add(it.optString("id"))) continue;   // 已经有了，跳过
            dlItems.add(it);
            added++;
        }
        dlOffset = dlItems.size();
        if (dlStatus != null)
            dlStatus.setText("共 " + dlCount + " 张（已显示 " + dlItems.size() + " 张）");
        if (added > 0) dlRender();
    }

    /** 纯本地重排网格：只用 dlItems + dlBmp 缓存，**不发请求**。
     *  删图/加星后走这里 —— 一格删掉后其它图会自动往前补，不会留空洞、也不会整页重拉。 */
    void dlRender() {
        if (dlGrid == null) return;
        dlGrid.removeAllViews();
        dlCells.clear();
        if (dlItems.isEmpty()) {
            dlGrid.addView(text("没有符合条件的图。", 12, C_SUB, false));
            return;
        }
        LinearLayout row = null;
        for (int i = 0; i < dlItems.size(); i++) {
            final JSONObject it = dlItems.get(i);
            final String id = it.optString("id");
            final String title = it.optString("title");
            final boolean fav = it.optBoolean("fav");
            if (i % 3 == 0) {
                row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                LinearLayout.LayoutParams lpr = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                lpr.setMargins(0, 0, 0, dp(8));
                row.setLayoutParams(lpr);
                dlGrid.addView(row);
            }
            LinearLayout cell = new LinearLayout(this);
            cell.setOrientation(LinearLayout.VERTICAL);
            cell.setPadding(dp(2), 0, dp(2), 0);
            LinearLayout.LayoutParams lpc = new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1);
            cell.setLayoutParams(lpc);

            ImageView iv = new ImageView(this);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            iv.setBackground(roundBg(C_BTN, C_LINE, 10));
            iv.setLayoutParams(new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, dp(112)));
            cell.addView(iv);

            TextView tv = text((fav ? "★ " : "") + (title.length() == 0 ? id : title),
                    11, C_SUB, false);
            tv.setMaxLines(1);
            tv.setEllipsize(android.text.TextUtils.TruncateAt.END);
            cell.addView(tv);

            cell.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { dlOpen(id, title, fav); }
            });
            row.addView(cell);
            dlCells.put(id, cell);

            Bitmap cached = dlBmp.get(id);
            if (cached != null) iv.setImageBitmap(cached);
            else dlLoadThumb(id, iv);
        }
        // 最后一行不满 3 个 → 补透明占位，避免剩下的图被拉宽
        int last = dlItems.size() % 3;
        if (row != null && last != 0) {
            for (int k = last; k < 3; k++) {
                LinearLayout pad = new LinearLayout(this);
                pad.setLayoutParams(new LinearLayout.LayoutParams(0, dp(1), 1));
                row.addView(pad);
            }
        }
    }

    /** 大图：按最长边缩到 maxSide 再解码（原图 1~3MB、3000px，直接 decode 会 OOM/解码失败 → 弹窗一片空白）。 */
    Bitmap httpBitmapSized(String path, int maxSide) {
        byte[] raw = httpBytes(path);
        if (raw == null || raw.length == 0) return null;
        try {
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(raw, 0, raw.length, o);
            int side = Math.max(o.outWidth, o.outHeight);
            int sample = 1;
            while (side / sample > maxSide * 2) sample *= 2;
            BitmapFactory.Options o2 = new BitmapFactory.Options();
            o2.inSampleSize = sample;
            o2.inPreferredConfig = Bitmap.Config.RGB_565;
            return BitmapFactory.decodeByteArray(raw, 0, raw.length, o2);
        } catch (Throwable t) {
            return null;
        }
    }

    void dlLoadThumb(final String id, final ImageView iv) {
        new Thread(new Runnable() {
            public void run() {
                final Bitmap bm = httpBitmap("/api/drawlib/thumb?id=" + id);
                if (bm == null) return;
                ui.post(new Runnable() {
                    public void run() { dlBmp.put(id, bm); iv.setImageBitmap(bm); }
                });
            }
        }).start();
    }

    void dlOpen(final String id, final String title, final boolean fav) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(12), dp(8), dp(12), dp(8));
        final ImageView iv = new ImageView(this);
        iv.setAdjustViewBounds(true);
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        iv.setMinimumHeight(dp(240));
        box.addView(iv);
        final TextView meta = text((title.length() == 0 ? id : title) + "（原图加载中…）", 12, C_SUB, false);
        meta.setPadding(0, dp(6), 0, 0);
        box.addView(meta);
        final String titleTxt = (title.length() == 0 ? id : title);
        // 2026-11-11：以前直接 httpBitmap 全尺寸解码 → 1.4MB/3000px 的原图会解码失败（OOM），
        // 弹窗里一片空白，用户看到就是"点了不出大图"。现在按最长边 1600 采样解码，失败再退缩略图。
        new Thread(new Runnable() {
            public void run() {
                Bitmap bm = httpBitmapSized("/api/drawlib/img?id=" + id, 1600);
                final boolean full = bm != null;
                if (bm == null) bm = httpBitmap("/api/drawlib/thumb?id=" + id);
                final Bitmap show = bm;
                ui.post(new Runnable() {
                    public void run() {
                        if (show != null) iv.setImageBitmap(show);
                        meta.setText(titleTxt + (show == null ? "（读取失败）" : (full ? "" : "（原图太大，这里显示缩略图）")));
                    }
                });
            }
        }).start();
        new AlertDialog.Builder(this)
                .setTitle("画图图库")
                .setView(box)
                .setPositiveButton(fav ? "取消收藏" : "收藏", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) { dlFav(id, title, !fav); }
                })
                .setNeutralButton("删除", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) { dlDel(id); }
                })
                .setNegativeButton("关闭", null)
                .show();
    }

    void dlFav(final String id, final String title, boolean f) {
        JSONObject b = new JSONObject();
        try { b.put("id", id); b.put("fav", f); } catch (Exception e) { }
        final boolean want = f;
        postJsonBody("/api/drawlib/edit", b, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { toast("收藏失败：" + err); return; }
                toast(want ? "已加星" : "已取消");
                // 就地改内存里的那条，再纯本地重排（缩略图走缓存，不发请求、不重下）
                int idx = -1;
                for (int i = 0; i < dlItems.size(); i++) {
                    if (id.equals(dlItems.get(i).optString("id"))) { idx = i; break; }
                }
                if (idx >= 0) {
                    try { dlItems.get(idx).put("fav", want); } catch (Exception e) { }
                    if (dlFavOnly && !want) {
                        dlItems.remove(idx);
                        dlCount = Math.max(0, dlCount - 1);
                    }
                    dlOffset = dlItems.size();
                    if (dlStatus != null)
                        dlStatus.setText("共 " + dlCount + " 张（已显示 " + dlItems.size() + " 张）");
                    dlRender();
                }
            }
        });
    }

    /** 就地把一张从列表里拿掉并重排（不重新请求列表）。 */
    void dlRemoveCell(String id) {
        dlBmp.remove(id);
        for (int i = 0; i < dlItems.size(); i++) {
            if (id.equals(dlItems.get(i).optString("id"))) { dlItems.remove(i); break; }
        }
        dlOffset = dlItems.size();
        dlCount = Math.max(0, dlCount - 1);
        if (dlStatus != null)
            dlStatus.setText("共 " + dlCount + " 张（已显示 " + dlItems.size() + " 张）");
        dlRender();
    }

    void dlDel(final String id) {
        JSONObject b = new JSONObject();
        try { b.put("id", id); } catch (Exception e) { }
        postJsonBody("/api/drawlib/del", b, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { toast("删除失败：" + err); return; }
                toast("已删除");
                dlRemoveCell(id);      // 只拿掉这一格，别重拉整页
            }
        });
    }

    void apiSave() {
        String nm = apName == null ? "" : apName.getText().toString().trim();
        String bs = apBase == null ? "" : apBase.getText().toString().trim();
        String md = apModel == null ? "" : apModel.getText().toString().trim();
        String ky = apKey == null ? "" : apKey.getText().toString().trim();
        // 2026-11-11 修「改 key 用不了」：这里原来要求 名字+地址+模型 全填，
        // 而界面提示写着"只想换 key：名字 + Key 填上就行" —— 于是只填名字+Key 被自己拦住。
        // 现在只要求名字；其余留空由后端沿用该渠道已保存的值。
        if (nm.length() == 0) {
            toast("至少填个名字（它同时也是渠道的标识）");
            return;
        }
        if (bs.length() == 0 && md.length() == 0 && ky.length() == 0) {
            toast("至少填一项：地址 / 模型 / Key");
            return;
        }
        JSONObject body = new JSONObject();
        try {
            body.put("name", nm);
            if (bs.length() > 0) body.put("base", bs);
            if (md.length() > 0) body.put("model", md);
            if (ky.length() > 0) body.put("key", ky);
        } catch (Exception e) { }
        if (apiStatus != null) apiStatus.setText("保存中…");
        postJsonBody("/api/apis/save", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (apiStatus != null) apiStatus.setText("保存失败：" + err);
                    return;
                }
                if (o != null && !o.optBoolean("ok")) {
                    if (apiStatus != null) apiStatus.setText(o.optString("msg", "保存失败"));
                    return;
                }
                if (apKey != null) apKey.setText("");
                apiPaint(o);
            }
        });
    }

    /** 真打一次验证渠道/key（/api/apis/test）。名字空 = 测当前生效渠道。 */
    void apiTest() {
        String nm = apName == null ? "" : apName.getText().toString().trim();
        String bs = apBase == null ? "" : apBase.getText().toString().trim();
        String md = apModel == null ? "" : apModel.getText().toString().trim();
        String ky = apKey == null ? "" : apKey.getText().toString().trim();
        JSONObject body = new JSONObject();
        try {
            if (nm.length() > 0) body.put("name", nm);
            if (bs.length() > 0) body.put("base", bs);
            if (md.length() > 0) body.put("model", md);
            if (ky.length() > 0) body.put("key", ky);   // 用刚填的 key 试，不必先保存
        } catch (Exception e) { }
        if (apiStatus != null) apiStatus.setText("测试中…（打一次真实请求）");
        postJsonBody("/api/apis/test", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (apiStatus == null) return;
                if (err != null) { apiStatus.setText("❌ 测试失败：" + err); return; }
                boolean ok = o != null && o.optBoolean("ok");
                apiStatus.setText((ok ? "✅ " : "❌ ") + (o == null ? "无返回" : o.optString("msg")));
            }
        });
    }

    void apiUse(String name) {
        JSONObject body = new JSONObject();
        try { body.put("name", name); } catch (Exception e) { }
        postJsonBody("/api/apis/use", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (apiStatus != null) apiStatus.setText("切换失败：" + err);
                    return;
                }
                apiPaint(o);
            }
        });
    }

    void apiDel(String name) {
        JSONObject body = new JSONObject();
        try { body.put("name", name); } catch (Exception e) { }
        postJsonBody("/api/apis/del", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (apiStatus != null) apiStatus.setText("删除失败：" + err);
                    return;
                }
                apiPaint(o);
            }
        });
    }

    // ============================================================ 图库（教她认图）
    static final java.util.Map<String, Boolean> foldOpen = new java.util.HashMap<String, Boolean>();

    /// 把已经建好的卡片改成可折叠：标题留在头部（右侧加箭头），其余内容收进可折叠 body。
    /// 这样写不用动原来那些 card() 的构建代码，风险最小。
    LinearLayout foldExisting(LinearLayout card, boolean defOpen) {
        if (card == null || card.getChildCount() == 0) return card;
        View titleV = card.getChildAt(0);
        String key = (titleV instanceof TextView) ? ((TextView) titleV).getText().toString() : "";
        boolean open = (key.length() > 0 && foldOpen.containsKey(key))
                ? foldOpen.get(key).booleanValue() : defOpen;

        card.removeViewAt(0);
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        head.addView(titleV, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        final TextView arrow = text(open ? "收起 ▴" : "展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        card.addView(head, 0);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        while (card.getChildCount() > 1) {
            View v = card.getChildAt(1);
            card.removeViewAt(1);
            body.addView(v);
        }
        body.setVisibility(open ? View.VISIBLE : View.GONE);
        card.addView(body);

        final String k = key;
        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (k.length() > 0) foldOpen.put(k, Boolean.valueOf(show));
            }
        });
        return card;
    }

    static final int REQ_PICK_LIB = 0x7042;
    LinearLayout plListBox;
    TextView plStatus;
    EditText plName, plDesc, plTags;
    final java.util.HashMap<String, LinearLayout> plCols = new java.util.HashMap<String, LinearLayout>();
    LinearLayout plTagRow;
    TextView plTagToggle;
    boolean plTagOpen = false;
    TextView plAutoBtn, plAutoInfo;
    boolean plAutoOn = true;
    String plTagGroupOpen = "";          // 当前展开的首字母（空 = 全收起）
    String plNameOpen = "";              // 当前展开的同名角色（空 = 全收起）
    EditText plTagSearch;
    String plTagQuery = "";
    // 标签库数据：{标签, 次数, 全拼, 首字母}（拼音搜索用，2026-11-02 用户要求）
    final java.util.List<String[]> plTagData = new java.util.ArrayList<String[]>();
    String plSimA = "", plSimB = "";
    String plFilterTag = "";
    // 图集（相册）2026-11-03：{id, 名字, 张数}
    final java.util.List<String[]> plAlbums = new java.util.ArrayList<String[]>();
    String plAlbumId = "";               // 当前进入的图集（空 = 相册首页）
    String plDefaultAlbum = "";
    boolean plSelMode = false;           // 多选移动模式
    final java.util.List<String> plSel = new java.util.ArrayList<String>();
    final java.util.HashMap<String, Bitmap> plBmp = new java.util.HashMap<String, Bitmap>();
    JSONObject plLastJson;
    final java.util.List<String> plTagNames = new java.util.ArrayList<String>();

    LinearLayout buildLibCard() {
        LinearLayout c = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text("记忆图库（教她认图）", 16, C_TXT, true);
        t.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        head.addView(t);
        final TextView arrow = text("展开 ▾", 13, C_SUB, false);
        arrow.setPadding(dp(8), dp(4), dp(8), dp(4));
        head.addView(arrow);
        c.addView(head);

        final LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setVisibility(View.GONE);
        c.addView(body);

        head.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                boolean show = body.getVisibility() != View.VISIBLE;
                body.setVisibility(show ? View.VISIBLE : View.GONE);
                arrow.setText(show ? "收起 ▴" : "展开 ▾");
                if (show) plLoadAll();
            }
        });

        TextView tip = text("存一张图 + 备注 + 标签，她就记住了（标签用来按词挑图）。以后任何人发同一张图"
                + "（转发、压缩、缩放都算），她先查图库、命中就直接按备注说，不去瞎猜。", 12, C_SUB, false);
        tip.setPadding(0, dp(6), 0, dp(10));
        body.addView(tip);

        plName = plInput("名字（如 doro）");
        body.addView(plName);
        plDesc = plInput("描述（她会照这个说）");
        body.addView(plDesc);
        plTags = plInput("标签（逗号分隔：白毛,猫耳）");
        body.addView(plTags);

        // 标签库：172 个标签一排就是好几屏 → 收成一行，点开才铺开
        // （2026-11-02 用户：「把这个标签放到抽屉里吧，这么多」）
        plTagToggle = text("标签库 ▸", 11, C_SUB, false);
        plTagToggle.setPadding(0, dp(10), 0, 0);
        plTagToggle.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                if (plTagRow == null) return;
                plToggleTags(plTagRow.getVisibility() != View.VISIBLE);
            }
        });
        body.addView(plTagToggle);
        // 标签搜索：中文 / 全拼 / 首字母都认（baimao、bm、白毛 → 白毛）
        plTagSearch = plInput("搜标签：中文 / 拼音 / 首字母");
        plTagSearch.setVisibility(View.GONE);
        plTagSearch.addTextChangedListener(new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence s, int st, int c, int a) {}
            public void onTextChanged(CharSequence s, int st, int b, int c) {}
            public void afterTextChanged(android.text.Editable s) {
                plTagQuery = (s == null ? "" : s.toString().trim().toLowerCase());
                plRenderTags();
            }
        });
        body.addView(plTagSearch);
        plTagRow = new LinearLayout(this);
        plTagRow.setOrientation(LinearLayout.VERTICAL);   // 多行：标签多了自动往下排
        plTagRow.setPadding(0, dp(6), 0, 0);
        plTagRow.setVisibility(View.GONE);                // 默认收起
        body.addView(plTagRow);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        Button pick = btn("选图并加入", C_ACCENT, 0xFFFFFFFF, "");
        pick.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { plPick(); }
        });
        Button reload = btn("刷新", 0, C_TXT, "");
        reload.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { plLoadAll(); }
        });
        LinearLayout.LayoutParams lp1 = new LinearLayout.LayoutParams(0, dp(46), 1);
        lp1.setMargins(0, dp(8), dp(8), dp(4));
        pick.setLayoutParams(lp1);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(dp(80), dp(46));
        lp2.setMargins(0, dp(8), 0, dp(4));
        reload.setLayoutParams(lp2);
        row.addView(pick);
        row.addView(reload);
        body.addView(row);

        // AI 自动打标签（2026-11-02 用户要求）：开关能在这儿改，但**识别只能在电脑控制台点**
        LinearLayout aiRow = new LinearLayout(this);
        aiRow.setOrientation(LinearLayout.HORIZONTAL);
        aiRow.setGravity(Gravity.CENTER_VERTICAL);
        aiRow.setPadding(0, dp(8), 0, 0);
        plAutoBtn = text("自动识图：…", 12, C_SUB, false);
        plAutoBtn.setPadding(dp(12), dp(6), dp(12), dp(6));
        plAutoBtn.setBackground(roundBg(C_BTN, C_LINE, 14));
        plAutoBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { plToggleAutoTag(); }
        });
        aiRow.addView(plAutoBtn);
        plAutoInfo = text("", 11, C_SUB, false);
        LinearLayout.LayoutParams aip = new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
        aip.setMargins(dp(8), 0, 0, 0);
        plAutoInfo.setLayoutParams(aip);
        aiRow.addView(plAutoInfo);
        body.addView(aiRow);
        TextView aiHint = text("识别要点电脑控制台的「立即识别」（手机不跑本机 DSH）", 10, C_SUB, false);
        aiHint.setPadding(0, dp(4), 0, 0);
        body.addView(aiHint);

        plStatus = text("", 12, C_SUB, false);
        plStatus.setPadding(0, dp(6), 0, dp(4));
        body.addView(plStatus);

        plListBox = new LinearLayout(this);
        plListBox.setOrientation(LinearLayout.VERTICAL);
        body.addView(plListBox);
        return c;
    }

    EditText plInput(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setHintTextColor(C_SUB);
        e.setTextColor(C_TXT);
        e.setTextSize(14);
        e.setSingleLine(true);
        e.setBackground(roundBg(C_BTN, C_LINE, 12));
        e.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, dp(6), 0, 0);
        e.setLayoutParams(lp);
        return e;
    }

    void plSetFilter(String tag) {
        plFilterTag = tag.equals(plFilterTag) ? "" : tag;
        plTagNames.clear();
        plPaint(plLastJson);
        plLoadTags();
    }

    void plMergeTags(String from, String to) {
        if (plStatus != null) plStatus.setText("正在把「" + from + "」并成「" + to + "」…");
        JSONObject body = new JSONObject();
        try { body.put("from", from); body.put("to", to); } catch (Exception e) { }
        postJsonBody("/api/photolib/tag_merge", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("合并失败：" + err);
                    return;
                }
                if (plStatus != null) plStatus.setText(o == null ? "已合并" : o.optString("msg", "已合并"));
                plLoadAll();
            }
        });
    }

    /** 标签库抽屉：展开/收起（2026-11-02 用户：「把这个标签放到抽屉里吧，这么多」）。 */
    void plToggleTags(boolean open) {
        plTagOpen = open;
        if (plTagRow != null) plTagRow.setVisibility(open ? View.VISIBLE : View.GONE);
        if (plTagSearch != null) plTagSearch.setVisibility(open ? View.VISIBLE : View.GONE);
        plTagLabel(plTagData.size());
    }

    void plTagLabel(int n) {
        if (plTagToggle == null) return;
        plTagToggle.setText((n > 0 ? "标签库（" + n + " 个）" : "标签库") + (plTagOpen ? " ▾" : " ▸"));
        plTagToggle.setTextColor(plTagOpen ? C_ACCENT : C_SUB);
    }

    /** 标签属于哪个「首字母抽屉」：英文/数字走自身，中文用后端给的 py1 首字母。 */
    static String plTagGroupOf(String tag, String py1) {
        if (py1 != null && py1.length() > 0) {
            char c = Character.toUpperCase(py1.charAt(0));
            if (c >= 'A' && c <= 'Z') return String.valueOf(c);
        }
        if (tag != null && tag.length() > 0) {
            char c0 = Character.toUpperCase(tag.charAt(0));
            if (c0 >= 'A' && c0 <= 'Z') return String.valueOf(c0);
            if (c0 >= '0' && c0 <= '9') return "#";
        }
        return "#";
    }

    /** `q` 是不是 `s` 的子序列（模糊搜索用；2026-11-03 用户：「把标签搜索改成模糊搜索」）。 */
    static boolean plIsSubseq(String s, String q) {
        if (q == null || q.length() == 0) return true;
        if (s == null || s.length() == 0) return false;
        int j = 0;
        for (int i = 0; i < s.length() && j < q.length(); i++)
            if (s.charAt(i) == q.charAt(j)) j++;
        return j == q.length();
    }

    /** 单关键词贴合度：0 不匹配；100 相等 / 80 开头 / 60 包含 / 30 子序列。
     *  子序列只在关键词≥3 个字母或含中文时才用（否则 `bm` 会连「半透明洋装」都匹配，噪音太大）。 */
    static int plTagScoreOne(String tag, String py, String ini, String q) {
        String t = tag == null ? "" : tag.toLowerCase();
        String p = py == null ? "" : py.toLowerCase();
        String n = ini == null ? "" : ini.toLowerCase();
        if (t.equals(q) || p.equals(q) || n.equals(q)) return 100;
        if (t.startsWith(q) || n.startsWith(q) || p.startsWith(q)) return 80;
        if (t.contains(q) || p.contains(q) || n.contains(q)) return 60;
        boolean allowSub = q.length() >= 3;
        if (!allowSub) {
            for (int i = 0; i < q.length(); i++) if (q.charAt(i) > 127) { allowSub = true; break; }
        }
        if (allowSub && (plIsSubseq(t, q) || plIsSubseq(p, q) || plIsSubseq(n, q))) return 30;
        return 0;
    }

    /** 整条查询：空格分多个词，每个都得中（AND），分数累加。 */
    static int plTagScore(String tag, String py, String ini, String q) {
        if (q == null || q.length() == 0) return 1;
        int total = 0;
        for (String piece : q.split("\\s+")) {
            if (piece.length() == 0) continue;
            int s = plTagScoreOne(tag, py, ini, piece);
            if (s == 0) return 0;
            total += s;
        }
        return total;
    }

    /** 命中（模糊：中文/拼音/首字母，少字母、顺序乱、多个词都认）。 */
    static boolean plTagHit(String tag, String py, String ini, String q) {
        return plTagScore(tag, py, ini, q) > 0;
    }

    void plLoadTags() {
        if (plTagRow == null) return;
        getJson("/api/photolib/tags", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (plTagRow == null) return;
                plTagData.clear();
                plTagNames.clear();
                if (err != null) {
                    plTagRow.removeAllViews();
                    plTagRow.addView(text("标签库读取失败：" + err, 11, C_SUB, false));
                    plTagLabel(0);
                    return;
                }
                JSONArray arr = o == null ? null : o.optJSONArray("tags");
                if (arr != null) {
                    for (int i = 0; i < arr.length(); i++) {
                        JSONObject it = arr.optJSONObject(i);
                        if (it == null) continue;
                        String tag = it.optString("tag");
                        if (tag.length() == 0) continue;
                        plTagData.add(new String[]{tag, "" + it.optInt("count", 0),
                                it.optString("py").toLowerCase(), it.optString("py1").toLowerCase()});
                        plTagNames.add(tag);
                    }
                }
                plSimA = "";
                plSimB = "";
                JSONArray sim = o == null ? null : o.optJSONArray("similar");
                if (sim != null && sim.length() > 0) {
                    JSONObject p0 = sim.optJSONObject(0);
                    if (p0 != null) { plSimA = p0.optString("a"); plSimB = p0.optString("b"); }
                }
                plTagLabel(plTagData.size());
                plRenderTags();
            }
        });
    }

    /** 按搜索词重画标签胶囊（拼音 / 中文都认）。 */
    void plRenderTags() {
        if (plTagRow == null) return;
        plTagRow.removeAllViews();
        if (plTagData.isEmpty()) {
            plTagRow.addView(text(plTagQuery.length() > 0 ? "没有匹配的标签" : "还没有标签：给图填一次就有了", 11, C_SUB, false));
            return;
        }
        if (plFilterTag.length() > 0) {
            TextView clr = text("显示全部", 12, C_BAD, false);
            clr.setPadding(dp(12), dp(6), dp(12), dp(6));
            clr.setBackground(roundBg(C_BTN, C_LINE, 14));
            LinearLayout.LayoutParams clp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            clp.setMargins(0, 0, dp(8), 0);
            clr.setLayoutParams(clp);
            clr.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plFilterTag = ""; plTagNames.clear(); plPaint(plLastJson); plLoadTags(); }
            });
            plTagRow.addView(clr);
        }
        // 通讯录式分组（2026-11-03 用户：「像手机通讯录一样，每个字母一个抽屉」）
        // 搜索时：模糊匹配 + **每组内部按贴合度排**（相等 > 开头 > 包含 > 子序列）
        final java.util.HashMap<String[], Integer> scores = new java.util.HashMap<String[], Integer>();
        java.util.TreeMap<String, java.util.List<String[]>> groups =
                new java.util.TreeMap<String, java.util.List<String[]>>();
        boolean searching = plTagQuery.length() > 0;
        for (String[] row : plTagData) {
            int sc = plTagScore(row[0], row[2], row[3], plTagQuery);
            if (sc <= 0) continue;
            String key = plTagGroupOf(row[0], row[3]);
            java.util.List<String[]> list = groups.get(key);
            if (list == null) { list = new java.util.ArrayList<String[]>(); groups.put(key, list); }
            list.add(row);
            scores.put(row, sc);
        }
        if (searching) {
            for (java.util.Map.Entry<String, java.util.List<String[]>> e : groups.entrySet()) {
                java.util.Collections.sort(e.getValue(), new java.util.Comparator<String[]>() {
                    public int compare(String[] a, String[] b) {
                        Integer sa = scores.get(a), sb = scores.get(b);
                        return (sb == null ? 0 : sb) - (sa == null ? 0 : sa);   // 分高的在前
                    }
                });
            }
        }
        // 「#」排最后
        java.util.List<String[]> sharpList = groups.remove("#");
        if (searching) plTagGroupOpen = "";

        // 顶部字母条
        LinearLayout bar = null;
        int inBar = 0;
        int idx = 0;
        for (java.util.Map.Entry<String, java.util.List<String[]>> e : groups.entrySet()) {
            if (bar == null || inBar >= 8) {
                bar = new LinearLayout(MainActivity.this);
                bar.setOrientation(LinearLayout.HORIZONTAL);
                bar.setPadding(0, 0, 0, dp(4));
                plTagRow.addView(bar);
                inBar = 0;
            }
            final String letter = e.getKey();
            boolean on = letter.equals(plTagGroupOpen);
            TextView lb = text(letter + " " + e.getValue().size(), 11,
                    on ? C_ACCENT : C_SUB, false);
            lb.setPadding(dp(9), dp(4), dp(9), dp(5));
            lb.setBackground(roundBg(on ? C_BTN : C_BG, C_LINE, 12));
            LinearLayout.LayoutParams lbp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lbp.setMargins(0, 0, dp(4), 0);
            lb.setLayoutParams(lbp);
            lb.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    plTagGroupOpen = letter.equals(plTagGroupOpen) ? "" : letter;
                    plRenderTags();
                }
            });
            bar.addView(lb);
            inBar++;
            idx++;
        }
        if (sharpList != null && !sharpList.isEmpty()) {
            if (bar == null || inBar >= 8) {
                bar = new LinearLayout(MainActivity.this);
                bar.setOrientation(LinearLayout.HORIZONTAL);
                bar.setPadding(0, 0, 0, dp(4));
                plTagRow.addView(bar);
                inBar = 0;
            }
            final String sharp = "#";
            boolean on = sharp.equals(plTagGroupOpen);
            TextView lb = text("# " + sharpList.size(), 11, on ? C_ACCENT : C_SUB, false);
            lb.setPadding(dp(9), dp(4), dp(9), dp(5));
            lb.setBackground(roundBg(on ? C_BTN : C_BG, C_LINE, 12));
            lb.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    plTagGroupOpen = sharp.equals(plTagGroupOpen) ? "" : sharp;
                    plRenderTags();
                }
            });
            bar.addView(lb);
        }

        // 展开的那一组
        int shown = 0;
        for (java.util.Map.Entry<String, java.util.List<String[]>> e : groups.entrySet()) {
            if (!searching && !e.getKey().equals(plTagGroupOpen)) continue;
            LinearLayout curRow = null;
            int inRow = 0;
            for (String[] row : e.getValue()) {
                if (curRow == null || inRow >= 4) {
                    curRow = new LinearLayout(MainActivity.this);
                    curRow.setOrientation(LinearLayout.HORIZONTAL);
                    curRow.setPadding(0, 0, 0, dp(6));
                    plTagRow.addView(curRow);
                    inRow = 0;
                }
                curRow.addView(plTagChip(row[0], Integer.parseInt(row[1])));
                inRow++;
                shown++;
            }
        }
        if (!searching && plTagGroupOpen.length() == 0) shown = 0;
        if (sharpList != null && !sharpList.isEmpty() && (searching || "#".equals(plTagGroupOpen))) {
            if (!searching && "#".equals(plTagGroupOpen)) {
                LinearLayout curRow = null;
                int inRow = 0;
                for (String[] row : sharpList) {
                    if (curRow == null || inRow >= 4) {
                        curRow = new LinearLayout(MainActivity.this);
                        curRow.setOrientation(LinearLayout.HORIZONTAL);
                        curRow.setPadding(0, 0, 0, dp(6));
                        plTagRow.addView(curRow);
                        inRow = 0;
                    }
                    curRow.addView(plTagChip(row[0], Integer.parseInt(row[1])));
                    inRow++;
                    shown++;
                }
            }
        }

        TextView all = text(searching
                        ? "匹配 " + shown + " 个 / 共 " + plTagData.size() + " 个（已全部展开）"
                        : (plTagGroupOpen.length() > 0
                           ? "「" + plTagGroupOpen + "」组 " + shown + " 个 · 共 " + plTagData.size() + " 个标签"
                           : "共 " + plTagData.size() + " 个标签 · 点上面任一字母展开"), 10, C_SUB, false);
        all.setPadding(0, dp(2), 0, dp(2));
        plTagRow.addView(all);
        // 相近标签：一键合并（服务器给的建议；搜索时先不显示，免得乱）
        if (plSimA.length() > 0 && plSimB.length() > 0 && plTagQuery.length() == 0) {
            String keep = plSimA.length() >= plSimB.length() ? plSimA : plSimB;
            String drop = plSimA.length() >= plSimB.length() ? plSimB : plSimA;
            TextView mg = text("合并「" + drop + "」→「" + keep + "」", 12, C_OK, false);
            mg.setPadding(dp(12), dp(6), dp(12), dp(6));
            mg.setBackground(roundBg(C_BTN, C_LINE, 14));
            final String fa = drop, fb = keep;
            mg.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plMergeTags(fa, fb); }
            });
            plTagRow.addView(mg);
        }
    }

    View plTagChip(final String tag, int cnt) {
        String label = cnt > 1 ? tag + " " + cnt : tag;
        TextView c = text(label, 12, C_ACCENT, false);
        c.setPadding(dp(12), dp(6), dp(12), dp(6));
        c.setBackground(roundBg(C_BTN, C_LINE, 14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, dp(8), 0);
        c.setLayoutParams(lp);
        c.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { plToggleTag(tag); }
        });
        c.setOnLongClickListener(new View.OnLongClickListener() {
            public boolean onLongClick(View v) {
                plSetFilter(tag);
                return true;
            }
        });
        return c;
    }

    void plToggleTag(String tag) {
        if (plTags == null) return;
        String cur = plTags.getText().toString();
        java.util.List<String> list = new java.util.ArrayList<String>();
        for (String p : cur.split("[,， 、;；]+")) {
            String t = p.trim();
            if (t.length() > 0 && !list.contains(t)) list.add(t);
        }
        if (list.contains(tag)) list.remove(tag); else list.add(tag);
        StringBuilder sb = new StringBuilder();
        for (String t : list) {
            if (sb.length() > 0) sb.append(",");
            sb.append(t);
        }
        plTags.setText(sb.toString());
    }

    void plLoadAll() {
        plLoadAlbums();
        plLoad();
        plLoadTags();
        plLoadAutoTag();
    }

    /** AI 自动打标签的状态（开关 + 待识别张数 + 上次结果）。 */
    void plLoadAutoTag() {
        getJson("/api/photolib/autotag", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plAutoInfo != null) plAutoInfo.setText("状态读取失败：" + err);
                    return;
                }
                plApplyAutoTag(o);
            }
        });
    }

    void plApplyAutoTag(JSONObject o) {
        if (o == null) return;
        plAutoOn = o.optBoolean("enabled", true);
        int q = o.optInt("queue", 0);
        int empty = o.optInt("empty", 0);
        if (plAutoBtn != null) {
            plAutoBtn.setText(plAutoOn ? "自动识图：开（新图入队）" : "自动识图：关");
            plAutoBtn.setTextColor(plAutoOn ? C_ACCENT : C_SUB);
        }
        String last = "";
        JSONObject l = o.optJSONObject("last");
        if (l != null && l.optString("at").length() > 0) {
            last = " · 上次 " + l.optString("at") + " 成功 " + l.optInt("ok") + " / 失败 " + l.optInt("fail");
        }
        if (plAutoInfo != null) plAutoInfo.setText("待识别 " + q + " · 没标签 " + empty + last);
    }

    void plToggleAutoTag() {
        JSONObject body = new JSONObject();
        try { body.put("enabled", !plAutoOn); } catch (Exception e) { }
        postJsonBody("/api/photolib/autotag", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plAutoInfo != null) plAutoInfo.setText("保存失败：" + err);
                    return;
                }
                plApplyAutoTag(o);
            }
        });
    }

    void plLoadThumb(final String id, final ImageView iv) {
        Bitmap cached = plBmp.get(id);
        if (cached != null) { iv.setImageBitmap(cached); return; }
        new Thread(new Runnable() {
            public void run() {
                // 磁盘缓存：重开控制台也不用重新下
                try {
                    java.io.File f0 = plThumbFile(id);
                    if (f0 != null && f0.exists() && f0.length() > 500) {
                        final Bitmap bm0 = BitmapFactory.decodeFile(f0.getAbsolutePath());
                        if (bm0 != null) {
                            ui.post(new Runnable() {
                                public void run() { plBmp.put(id, bm0); iv.setImageBitmap(bm0); }
                            });
                            return;
                        }
                    }
                } catch (Throwable t) { }
                byte[] raw = httpBytes("/api/photolib/thumb?id=" + id);
                if (raw == null || raw.length < 500) return;
                final Bitmap bm = BitmapFactory.decodeByteArray(raw, 0, raw.length);
                if (bm == null) return;
                try {
                    java.io.File f = plThumbFile(id);
                    if (f != null) {
                        java.io.FileOutputStream fo = new java.io.FileOutputStream(f);
                        fo.write(raw);
                        fo.close();
                    }
                } catch (Throwable t) { }
                ui.post(new Runnable() {
                    public void run() { plBmp.put(id, bm); iv.setImageBitmap(bm); }
                });
            }
        }).start();
    }

    void plLoad() {
        if (plListBox == null) return;
        if (plStatus != null) plStatus.setText("读取中…");
        String path = "/api/photolib";
        if (plAlbumId.length() > 0) path += "?album=" + enc(plAlbumId);
        getJson(path, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("图库读取失败：" + err);
                    return;
                }
                plPaint(o);
            }
        });
    }

    /** 图集列表（相册首页用，也给编辑弹窗的下拉用）。 */
    void plLoadAlbums() {
        getJson("/api/photolib/albums", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) return;
                plAlbums.clear();
                JSONArray arr = o == null ? null : o.optJSONArray("albums");
                if (arr != null) {
                    for (int i = 0; i < arr.length(); i++) {
                        JSONObject a = arr.optJSONObject(i);
                        if (a == null) continue;
                        plAlbums.add(new String[]{a.optString("id"), a.optString("name"),
                                "" + a.optInt("count", 0)});
                    }
                }
                plDefaultAlbum = o == null ? "" : o.optString("default");
                plPaint(plLastJson);
            }
        });
    }

    void plPaint(JSONObject o) {
        if (plListBox == null) return;
        if (o != null) plLastJson = o;
        plListBox.removeAllViews();
        plCols.clear();
        // 相册首页：没进图集时只显示图集封面（2026-11-03 用户：照搬手机相册）
        if (plAlbumId.length() == 0 && plFilterTag.length() == 0) {
            plPaintAlbums();
            return;
        }
        int count = o == null ? 0 : o.optInt("count", 0);
        if (plStatus != null) plStatus.setText(plFilterTag.length() > 0
                ? ("只看「" + plFilterTag + "」· 共 " + count + " 张（长按标签可取消）")
                : ("图集「" + plAlbumName(plAlbumId) + "」· 共 " + count + " 张"));
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        if (arr == null || arr.length() == 0) {
            plListBox.addView(text("还是空的：上面填名字、描述（可选标签），点「选图并加入」。", 12, C_SUB, false));
            return;
        }

        // 图集内的工具条：返回 / 多选 / 移到哪个集（2026-11-03）
        if (plAlbumId.length() > 0) {
            LinearLayout bar = new LinearLayout(this);
            bar.setOrientation(LinearLayout.HORIZONTAL);
            bar.setGravity(Gravity.CENTER_VERTICAL);
            bar.setPadding(0, 0, 0, dp(8));
            TextView bk = text("← 相册", 12, C_ACCENT, false);
            bk.setPadding(dp(12), dp(6), dp(12), dp(6));
            bk.setBackground(roundBg(C_BTN, C_LINE, 12));
            bk.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    plAlbumId = ""; plFilterTag = ""; plSelMode = false; plSel.clear();
                    plLoadAlbums();
                }
            });
            bar.addView(bk);
            TextView sel = text(plSelMode ? "退出多选" : "多选", 12, C_TXT, false);
            sel.setPadding(dp(12), dp(6), dp(12), dp(6));
            sel.setBackground(roundBg(C_BTN, C_LINE, 12));
            LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            slp.setMargins(dp(8), 0, 0, 0);
            sel.setLayoutParams(slp);
            sel.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    plSelMode = !plSelMode;
                    if (!plSelMode) plSel.clear();
                    plPaint(plLastJson);
                }
            });
            bar.addView(sel);
            if (plSelMode) {
                TextView go = text("移到…（" + plSel.size() + " 张）", 12, C_OK, false);
                go.setPadding(dp(12), dp(6), dp(12), dp(6));
                go.setBackground(roundBg(C_BTN, C_LINE, 12));
                LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                glp.setMargins(dp(8), 0, 0, 0);
                go.setLayoutParams(glp);
                go.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) { plMoveDialog(); }
                });
                bar.addView(go);
            }
            plListBox.addView(bar);
        }

        // 同名角色抽屉（2026-11-03 用户：「顺便把同名角色也弄成抽屉」）
        // 同名 >1 的收成一行标题，点开才铺开；名字只有 1 张的直接平铺。
        java.util.LinkedHashMap<String, java.util.List<JSONObject>> byName =
                new java.util.LinkedHashMap<String, java.util.List<JSONObject>>();
        for (int i = 0; i < arr.length(); i++) {
            JSONObject it0 = arr.optJSONObject(i);
            if (it0 == null) continue;
            String nm0 = it0.optString("name");
            java.util.List<JSONObject> l0 = byName.get(nm0);
            if (l0 == null) { l0 = new java.util.ArrayList<JSONObject>(); byName.put(nm0, l0); }
            l0.add(it0);
        }
        java.util.List<JSONObject> singles = new java.util.ArrayList<JSONObject>();
        int groupsN = 0;
        for (java.util.Map.Entry<String, java.util.List<JSONObject>> e : byName.entrySet()) {
            java.util.List<JSONObject> l = e.getValue();
            if (l.size() <= 1) { singles.addAll(l); continue; }
            groupsN++;
            final String gname = e.getKey();
            boolean gopen = gname.equals(plNameOpen);
            TextView gh = text((gopen ? "▾ " : "▸ ") + gname + "  " + l.size() + " 张", 12,
                    gopen ? C_ACCENT : C_TXT, true);
            gh.setPadding(dp(10), dp(6), dp(10), dp(6));
            gh.setBackground(roundBg(gopen ? C_BTN : C_BG, C_LINE, 12));
            LinearLayout.LayoutParams ghp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            ghp.setMargins(0, dp(4), 0, dp(4));
            gh.setLayoutParams(ghp);
            gh.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    plNameOpen = gname.equals(plNameOpen) ? "" : gname;
                    plPaint(plLastJson);
                }
            });
            plListBox.addView(gh);
            if (!gopen) continue;
            for (JSONObject it : l) plAddRow(it);
        }
        if (!singles.isEmpty()) {
            if (groupsN > 0) {
                TextView sep = text("—— 单个的（" + singles.size() + " 张）——", 10, C_SUB, false);
                sep.setPadding(0, dp(8), 0, dp(2));
                plListBox.addView(sep);
            }
            for (JSONObject it : singles) plAddRow(it);
        }
    }

    /** 列表里的一行（WPF 那边叫 PhotoLibTile）：缩略图 + 名字 + 标签 + 改/删。 */
    String plAlbumName(String aid) {
        for (String[] a : plAlbums) if (aid.equals(a[0])) return a[1];
        return aid;
    }

    String plAlbumCover(String aid) {
        JSONArray arr = plLastJson == null ? null : plLastJson.optJSONArray("items");
        if (arr == null) return "";
        for (int i = 0; i < arr.length(); i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            if (aid.equals(it.optString("album")) && it.optString("id").length() > 0)
                return it.optString("id");
        }
        return "";
    }

    /** 相册首页：一格一个图集（封面 + 名字 + 张数），点进去看图。 */
    void plPaintAlbums() {
        if (plListBox == null) return;
        if (plAlbums.isEmpty()) {
            plListBox.addView(text("图集还没读出来（点「刷新」试试）", 12, C_SUB, false));
            return;
        }
        if (plStatus != null) plStatus.setText("图集 " + plAlbums.size() + " 个 · 点封面进去看");

        TextView nb = text("＋ 新建图集", 12, C_OK, false);
        nb.setPadding(dp(12), dp(7), dp(12), dp(7));
        nb.setBackground(roundBg(C_BTN, C_LINE, 12));
        nb.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { plAlbumNewDialog(); }
        });
        LinearLayout.LayoutParams nlp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        nlp.setMargins(0, 0, 0, dp(10));
        nb.setLayoutParams(nlp);
        plListBox.addView(nb);

        for (final String[] a : plAlbums) {
            final String aid = a[0];
            int cnt = 0;
            try { cnt = Integer.parseInt(a[2]); } catch (Exception e) { }
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(0, dp(6), 0, dp(6));

            ImageView iv = new ImageView(this);
            LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(dp(76), dp(76));
            tlp.setMargins(0, 0, dp(10), 0);
            iv.setLayoutParams(tlp);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            iv.setBackgroundColor(C_BTN);
            row.addView(iv);
            String cover = plAlbumCover(aid);
            if (cover.length() > 0) plLoadThumb(cover, iv);

            LinearLayout col = new LinearLayout(this);
            col.setOrientation(LinearLayout.VERTICAL);
            col.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            col.addView(text(a[1], 14, C_TXT, true));
            col.addView(text(cnt + " 张", 11, C_SUB, false));
            row.addView(col);

            TextView rn = text("改名", 11, C_ACCENT, false);
            rn.setPadding(dp(10), dp(5), dp(10), dp(5));
            rn.setBackground(roundBg(C_BTN, C_LINE, 12));
            rn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plAlbumRenameDialog(aid, a[1]); }
            });
            row.addView(rn);
            if (!aid.equals(plDefaultAlbum)) {
                TextView del = text("删", 11, C_BAD, false);
                del.setPadding(dp(10), dp(5), dp(10), dp(5));
                del.setBackground(roundBg(C_BTN, C_LINE, 12));
                LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                dlp.setMargins(dp(6), 0, 0, 0);
                del.setLayoutParams(dlp);
                final int fcnt = cnt;
                del.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) { plAlbumDeleteDialog(aid, a[1], fcnt); }
                });
                row.addView(del);
            }
            row.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plEnterAlbum(aid); }
            });
            plListBox.addView(row);
        }
    }

    void plEnterAlbum(String aid) {
        plAlbumId = aid == null ? "" : aid;
        plFilterTag = "";
        plSelMode = false;
        plSel.clear();
        if (plStatus != null) plStatus.setText("正在加载「" + plAlbumName(plAlbumId) + "」…");
        plLoad();
    }

    void plAlbumPost(final String path, JSONObject body) {
        postJsonBody(path, body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("保存失败：" + err);
                    return;
                }
                plLoadAlbums();
            }
        });
    }

    void plAlbumNewDialog() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(8), dp(16), dp(0));
        final EditText e = plInput("图集名字（比如「白毛」「夏日」）");
        box.addView(e);
        new AlertDialog.Builder(this)
                .setTitle("新建图集")
                .setView(box)
                .setPositiveButton("建", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        String n = e.getText().toString().trim();
                        if (n.length() == 0) return;
                        JSONObject b = new JSONObject();
                        try { b.put("name", n); } catch (Exception ex) { }
                        plAlbumPost("/api/photolib/album/new", b);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void plAlbumRenameDialog(final String aid, String old) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(8), dp(16), dp(0));
        final EditText e = plInput("新名字");
        e.setText(old);
        box.addView(e);
        new AlertDialog.Builder(this)
                .setTitle("图集改名")
                .setView(box)
                .setPositiveButton("改名", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        String n = e.getText().toString().trim();
                        if (n.length() == 0) return;
                        JSONObject b = new JSONObject();
                        try { b.put("id", aid); b.put("name", n); } catch (Exception ex) { }
                        plAlbumPost("/api/photolib/album/rename", b);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void plAlbumDeleteDialog(final String aid, String name, int count) {
        new AlertDialog.Builder(this)
                .setTitle("删除图集")
                .setMessage("删掉「" + name + "」？里面的 " + count + " 张图不会删，会回到「未分类」。")
                .setPositiveButton("删除", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        JSONObject b = new JSONObject();
                        try { b.put("id", aid); } catch (Exception ex) { }
                        plAlbumPost("/api/photolib/album/del", b);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    /** 多选后选目标图集。 */
    void plMoveDialog() {
        if (plSel.isEmpty()) {
            if (plStatus != null) plStatus.setText("先点图片选中几张");
            return;
        }
        final java.util.List<String> ids = new java.util.ArrayList<String>();
        final java.util.List<String> names = new java.util.ArrayList<String>();
        for (String[] a : plAlbums) { ids.add(a[0]); names.add(a[1]); }
        new AlertDialog.Builder(this)
                .setTitle("把 " + plSel.size() + " 张移到…")
                .setItems(names.toArray(new String[0]), new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int which) {
                        JSONArray ja = new JSONArray();
                        for (String s : plSel) ja.put(s);
                        JSONObject b = new JSONObject();
                        try { b.put("ids", ja); b.put("album", ids.get(which)); } catch (Exception ex) { }
                        plSelMode = false;
                        plSel.clear();
                        plAlbumPost("/api/photolib/album/move", b);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void plAddRow(JSONObject it) {
        {
            final String id = it.optString("id");
            final String nm = it.optString("name");
            String ds = it.optString("desc");
            int hits = it.optInt("hits", 0);
            LinearLayout r = new LinearLayout(this);
            r.setOrientation(LinearLayout.HORIZONTAL);
            r.setGravity(Gravity.CENTER_VERTICAL);
            r.setPadding(0, dp(7), 0, dp(7));
            // 多选模式：整行点一下选中/取消（2026-11-03「多选移动」）
            if (plSelMode) {
                final boolean chosen = plSel.contains(id);
                if (chosen) r.setBackground(roundBg(C_BTN, C_ACCENT, 10));
                r.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) {
                        if (plSel.contains(id)) plSel.remove(id); else plSel.add(id);
                        plPaint(plLastJson);
                    }
                });
            }
            JSONArray tgs = it.optJSONArray("tags");
            String tagStr = "";
            if (tgs != null && tgs.length() > 0) {
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < tgs.length(); k++) {
                    if (sb.length() > 0) sb.append(",");
                    sb.append(tgs.optString(k));
                }
                tagStr = "  [" + sb + "]";
            }
            ImageView th = new ImageView(this);
            LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(dp(68), dp(68));
            tlp.setMargins(0, 0, dp(10), 0);
            th.setLayoutParams(tlp);
            th.setScaleType(ImageView.ScaleType.CENTER_CROP);
            th.setBackgroundColor(C_BTN);
            r.addView(th);
            plLoadThumb(id, th);

            LinearLayout col = new LinearLayout(this);
            col.setOrientation(LinearLayout.VERTICAL);
            col.setLayoutParams(new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            col.addView(text(nm, 13, C_TXT, true));
            if (tagStr.length() > 0) col.addView(text(tagStr.trim(), 11, C_ACCENT, false));
            if (ds.length() > 0) col.addView(text(ds.length() > 22 ? ds.substring(0, 22) + "…" : ds, 11, C_SUB, false));
            col.addView(text("命中 " + hits, 10, C_SUB, false));
            r.addView(col);
            plCols.put(id, col);
            Button ed = btn("改", 0, C_TXT, "");
            LinearLayout.LayoutParams elp = new LinearLayout.LayoutParams(dp(48), dp(38));
            elp.setMargins(dp(6), 0, 0, 0);
            ed.setLayoutParams(elp);
            final String fdesc = ds;
            final String ftags = tagStr.replace("[", "").replace("]", "").trim();
            ed.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plEdit(id, nm, fdesc, ftags); }
            });
            r.addView(ed);
            Button del = btn("删", 0, C_BAD, "");
            LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(48), dp(38));
            dlp.setMargins(dp(6), 0, 0, 0);
            del.setLayoutParams(dlp);
            del.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { plDel(id, nm); }
            });
            r.addView(del);
            plListBox.addView(r);
        }
    }
    void plEdit(final String id, String name, String desc, String tags) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(6), dp(16), dp(0));
        final EditText eName = plInput("名字（同名可挂多张图）");
        eName.setText(name);
        final EditText eDesc = plInput("描述（她照这个说，也可以写特征）");
        eDesc.setText(desc);
        eDesc.setSingleLine(false);
        eDesc.setMinLines(3);
        eDesc.setGravity(Gravity.TOP);
        final EditText eTags = plInput("标签（逗号分隔：白毛,猫耳）");
        eTags.setText(tags);
        box.addView(text("名字", 11, C_SUB, false));
        box.addView(eName);
        box.addView(text("描述", 11, C_SUB, false));
        box.addView(eDesc);
        box.addView(text("标签", 11, C_SUB, false));
        box.addView(eTags);
        // 图集下拉（2026-11-03 用户：改标签时一键分到哪个相册）
        final java.util.List<String> albIds = new java.util.ArrayList<String>();
        final java.util.List<String> albNames = new java.util.ArrayList<String>();
        final int[] albPick = {0};
        String curAlbum = it_album_of(id);
        for (String[] a : plAlbums) {
            albIds.add(a[0]);
            albNames.add(a[1]);
            if (a[0].equals(curAlbum)) albPick[0] = albIds.size() - 1;
        }
        if (!albNames.isEmpty()) {
            box.addView(text("图集（放进哪个相册）", 11, C_SUB, false));
            TextView albBtn = text("📁 " + albNames.get(albPick[0]), 12, C_ACCENT, false);
            albBtn.setPadding(dp(12), dp(7), dp(12), dp(7));
            albBtn.setBackground(roundBg(C_BTN, C_LINE, 12));
            final TextView albBtnRef = albBtn;
            albBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    new AlertDialog.Builder(MainActivity.this)
                            .setTitle("放进哪个相册")
                            .setItems(albNames.toArray(new String[0]), new DialogInterface.OnClickListener() {
                                public void onClick(DialogInterface d, int which) {
                                    albPick[0] = which;
                                    albBtnRef.setText("📁 " + albNames.get(which));
                                }
                            })
                            .show();
                }
            });
            box.addView(albBtn);
        }
        if (!plTagNames.isEmpty()) {
            box.addView(text("标签库（点一下加进上面那行；标签多了可以滚）", 11, C_SUB, false));
            // 2026-11-02 用户：「标签弄成按字母顺序排」——服务端已按**拼音 A→Z** 返回，这里不重排
            java.util.List<String> sorted = new java.util.ArrayList<String>(plTagNames);
            LinearLayout rows = new LinearLayout(this);
            rows.setOrientation(LinearLayout.VERTICAL);
            LinearLayout curRow = null;
            int inRow = 0;
            final EditText box2 = eTags;
            for (String tg : sorted) {
                if (curRow == null || inRow >= 3) {
                    curRow = new LinearLayout(this);
                    curRow.setOrientation(LinearLayout.HORIZONTAL);
                    curRow.setPadding(0, 0, 0, dp(6));
                    rows.addView(curRow);
                    inRow = 0;
                }
                final String tname = tg;
                TextView chip2 = text(tg, 12, 0xFFDCE9FF, false);          // 蓝白，深色底上更清楚
                chip2.setPadding(dp(12), dp(6), dp(12), dp(6));
                chip2.setBackground(roundBg(0xFF1B2A4A, 0xFF3E5E93, 14));
                LinearLayout.LayoutParams c2 = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                c2.setMargins(0, 0, dp(8), 0);
                chip2.setLayoutParams(c2);
                chip2.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) {
                        String cur = box2.getText().toString();
                        java.util.List<String> list = new java.util.ArrayList<String>();
                        for (String p : cur.split("[,， 、;；]+")) {
                            String tt = p.trim();
                            if (tt.length() > 0 && !list.contains(tt)) list.add(tt);
                        }
                        if (list.contains(tname)) list.remove(tname); else list.add(tname);
                        StringBuilder sb2 = new StringBuilder();
                        for (String tt : list) {
                            if (sb2.length() > 0) sb2.append(",");
                            sb2.append(tt);
                        }
                        box2.setText(sb2.toString());
                    }
                });
                curRow.addView(chip2);
                inRow++;
            }
            ScrollView tagScroll2 = new ScrollView(this);
            tagScroll2.setVerticalScrollBarEnabled(true);
            LinearLayout.LayoutParams slp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, dp(124));      // 固定高度，弹窗不再被标签撑大
            slp.setMargins(0, dp(4), 0, 0);
            tagScroll2.setLayoutParams(slp);
            tagScroll2.addView(rows);
            box.addView(tagScroll2);
        }
        ScrollView scrollBox = new ScrollView(this);
        scrollBox.addView(box);
        new AlertDialog.Builder(this)
                .setTitle("编辑图库条目")
                .setView(scrollBox)
                .setPositiveButton("保存", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        String aid = albIds.isEmpty() ? "" : albIds.get(albPick[0]);
                        plSaveEdit(id, eName.getText().toString(), eDesc.getText().toString(),
                                eTags.getText().toString(), aid);
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    String it_album_of(String id) {
        JSONArray arr = plLastJson == null ? null : plLastJson.optJSONArray("items");
        if (arr != null) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject it = arr.optJSONObject(i);
                if (it != null && id.equals(it.optString("id"))) return it.optString("album");
            }
        }
        return plDefaultAlbum;
    }

    void plSaveEdit(String id, String name, String desc, String tags, String album) {
        if (plStatus != null) plStatus.setText("保存中…");
        JSONObject body = new JSONObject();
        try {
            body.put("id", id);
            body.put("name", name.trim());
            body.put("desc", desc.trim());
            body.put("tags", tags.trim());
            if (album != null && album.length() > 0) body.put("album", album);
        } catch (Exception e) { }
        postJsonBody("/api/photolib/edit", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("保存失败：" + err);
                    return;
                }
                // 只刷新这一行，别整页重画（重画会把所有缩略图重新下一遍）
                if (plStatus != null) plStatus.setText("已保存");
                plPaint(o);            // 整页重画（缩略图有缓存，不重新下载）
                plLoadTags();
            }
        });
    }

    void plPick() {
        Intent i = new Intent(Intent.ACTION_GET_CONTENT);
        i.setType("image/*");
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        try {
            startActivityForResult(Intent.createChooser(i, "选图加入图库（可多选）"), REQ_PICK_LIB);
        } catch (Exception e) {
            toast("打不开选图界面：" + e.getMessage());
        }
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req == REQ_PICK_MK) {          // 梗库选梗包
            mkOnPick(req, res, data);
            return;
        }
        if (req == REQ_PICK_AVA) {         // 她的 QQ：换头像
            qqOnPickAvatar(res, data);
            return;
        }
        if (req == REQ_PICK_QIMG) {        // 她的 QQ：说说配图
            qqOnPickPostImages(res, data);
            return;
        }
        if (req == REQ_PICK_LIB) {
            plOnPickMulti(res, data);
            return;
        }
    }

    byte[] readAllBytes(InputStream is) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) > 0) bos.write(buf, 0, n);
        return bos.toByteArray();
    }

    /** 手机拍的图太大：先缩到 1280 以内再压 jpg，不然 base64 几十 MB */
    byte[] shrinkJpeg(byte[] raw) { return shrinkJpeg(raw, 1280); }

    byte[] shrinkJpeg(byte[] raw, int maxSide) {
        try {
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(raw, 0, raw.length, o);
            int max = Math.max(o.outWidth, o.outHeight);
            int scale = 1;
            while (max / scale > maxSide) scale *= 2;
            BitmapFactory.Options o2 = new BitmapFactory.Options();
            o2.inSampleSize = scale;
            Bitmap bm = BitmapFactory.decodeByteArray(raw, 0, raw.length, o2);
            if (bm == null) return raw;
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            bm.compress(Bitmap.CompressFormat.JPEG, 86, bos);
            byte[] out = bos.toByteArray();
            return (out.length > 0 && out.length < raw.length) ? out : raw;
        } catch (Throwable t) {
            return raw;
        }
    }

    void plOnPickMulti(int res, Intent data) {
        if (res != RESULT_OK || data == null) return;
        final java.util.List<byte[]> imgs = new java.util.ArrayList<byte[]>();
        try {
            android.content.ClipData cd = data.getClipData();
            if (cd != null && cd.getItemCount() > 0) {
                for (int i = 0; i < cd.getItemCount(); i++) {
                    android.net.Uri u = cd.getItemAt(i).getUri();
                    if (u == null) continue;
                    InputStream is = getContentResolver().openInputStream(u);
                    byte[] raw = readAllBytes(is);
                    if (is != null) is.close();
                    imgs.add(shrinkJpeg(raw));
                }
            } else if (data.getData() != null) {
                InputStream is = getContentResolver().openInputStream(data.getData());
                byte[] raw = readAllBytes(is);
                if (is != null) is.close();
                imgs.add(shrinkJpeg(raw));
            }
        } catch (Exception e) {
            toast("读图失败：" + e.getMessage());
            return;
        }
        if (imgs.isEmpty()) return;
        plUploadQueue(imgs, 0, 0);
    }

    void plUploadQueue(final java.util.List<byte[]> imgs, final int idx, final int okN) {
        if (idx >= imgs.size()) {
            if (plStatus != null) plStatus.setText("加了 " + okN + "/" + imgs.size() + " 张");
            plLoad();
            plLoadTags();
            return;
        }
        if (plStatus != null) plStatus.setText("正在上传 " + (idx + 1) + "/" + imgs.size() + "…");
        plUploadOne(imgs.get(idx), new PlDone() {
            public void on(boolean ok) { plUploadQueue(imgs, idx + 1, okN + (ok ? 1 : 0)); }
        });
    }

    interface PlDone { void on(boolean ok); }

    void plUpload(byte[] jpg) {
        plUploadOne(jpg, null);
    }

    void plUploadOne(final byte[] jpg, final PlDone cb) {
        String name = plName == null ? "" : plName.getText().toString().trim();
        String desc = plDesc == null ? "" : plDesc.getText().toString().trim();
        if (plStatus != null) plStatus.setText("正在上传（" + (jpg.length / 1024) + " KB）…");
        JSONObject body = new JSONObject();
        try {
            body.put("name", name);
            body.put("desc", desc);
            String tags = plTags == null ? "" : plTags.getText().toString().trim();
            body.put("tags", tags);
            body.put("by", "android");
            body.put("image_b64", android.util.Base64.encodeToString(jpg, android.util.Base64.NO_WRAP));
        } catch (Exception e) { }
        postJsonBody("/api/photolib/add", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("上传失败：" + err);
                    if (cb != null) cb.on(false);
                    return;
                }
                boolean ok = o != null && o.optBoolean("ok");
                if (plStatus != null)
                    plStatus.setText(ok ? o.optString("msg", "已加入") : o.optString("msg", "没加成"));
                if (ok) {
                    if (plName != null) plName.setText("");
                    if (plDesc != null) plDesc.setText("");
                    if (plTags != null) plTags.setText("");
                }
                plPaint(o);
                if (cb != null) cb.on(ok);
            }
        });
    }

    /** 保存后只重建某一行的文字列；找不到这一行就返回 false（让调用方整页重画）。 */
    boolean plRefreshRow(JSONObject o, String id) {
        LinearLayout col = plCols.get(id);
        JSONArray arr = o == null ? null : o.optJSONArray("items");
        if (col == null || arr == null) return false;
        JSONObject hit = null;
        for (int i = 0; i < arr.length(); i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it != null && id.equals(it.optString("id"))) { hit = it; break; }
        }
        if (hit == null) return false;
        col.removeAllViews();
        String nm = hit.optString("name");
        String ds = hit.optString("desc");
        int hits = hit.optInt("hits", 0);
        StringBuilder sb = new StringBuilder();
        JSONArray tgs = hit.optJSONArray("tags");
        if (tgs != null) {
            for (int k = 0; k < tgs.length(); k++) {
                if (sb.length() > 0) sb.append(" ");
                sb.append(tgs.optString(k));
            }
        }
        col.addView(text(nm, 13, C_TXT, true));
        if (sb.length() > 0) col.addView(text(sb.toString(), 11, C_ACCENT, false));
        if (ds.length() > 0) col.addView(text(ds.length() > 22 ? ds.substring(0, 22) + "…" : ds, 11, C_SUB, false));
        col.addView(text("命中 " + hits, 10, C_SUB, false));
        return true;
    }

    void plDel(String id, String name) {
        if (plStatus != null) plStatus.setText("正在删「" + name + "」…");
        JSONObject body = new JSONObject();
        try { body.put("key", id); } catch (Exception e) { }
        postJsonBody("/api/photolib/del", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (plStatus != null) plStatus.setText("删除失败：" + err);
                    return;
                }
                plPaint(o);
            }
        });
    }

    Button findBtn(View rootV, String label) {
        if (!(rootV instanceof ViewGroup)) return null;
        ViewGroup g = (ViewGroup) rootV;
        for (int i = 0; i < g.getChildCount(); i++) {
            View c = g.getChildAt(i);
            if (c instanceof Button && label.contentEquals(((Button) c).getText())) return (Button) c;
            Button r = findBtn(c, label);
            if (r != null) return r;
        }
        return null;
    }

    /** 把任意概率吸附到最近的挡位（读回旧值 / 手改过 json 时用）。 */
    static double ctlSnapPoke(double v) {
        if (v <= 0) return CTL_POKE_STEPS[0];
        double best = CTL_POKE_STEPS[0];
        double gap = Math.abs(v - best);
        for (int i = 0; i < CTL_POKE_STEPS.length; i++) {
            double g = Math.abs(v - CTL_POKE_STEPS[i]);
            if (g < gap) { best = CTL_POKE_STEPS[i]; gap = g; }
        }
        return best;
    }

    /** 默默发表情挡位吸附：<0 保持「用默认」，0 就是 0（这个群关掉）。 */
    static double ctlSnapAmbient(double v) {
        if (v < 0) return -1.0;
        double best = CTL_AMBIENT_STEPS[0];
        double gap = Math.abs(v - best);
        for (int i = 0; i < CTL_AMBIENT_STEPS.length; i++) {
            double g = Math.abs(v - CTL_AMBIENT_STEPS[i]);
            if (g < gap) { best = CTL_AMBIENT_STEPS[i]; gap = g; }
        }
        return best;
    }

    void ctlPaint() {
        if (ctlAtBtn != null) {
            ctlAtBtn.setText(ctlOnlyAt ? "已开启" : "关闭");
            ctlAtBtn.setBackground(roundBg(ctlOnlyAt ? C_ACCENT : C_BTN, C_LINE, 14));
            ctlAtBtn.setTextColor(ctlOnlyAt ? 0xFFFFFFFF : C_TXT);
        }
        if (ctlAiBtn != null) {
            ctlAiBtn.setText(ctlAi == 1 ? "本群开" : (ctlAi == 0 ? "本群关" : "跟随全局"));
            ctlAiBtn.setBackground(roundBg(ctlAi == 1 ? C_ACCENT : C_BTN, C_LINE, 14));
            ctlAiBtn.setTextColor(ctlAi == 1 ? 0xFFFFFFFF : (ctlAi == 0 ? 0xFFFFC2C2 : C_TXT));
        }
        if (ctlBurstBtn != null) {
            ctlBurstBtn.setText(ctlBurst == 1 ? "本群开" : (ctlBurst == 0 ? "本群关" : "跟随全局"));
            ctlBurstBtn.setBackground(roundBg(ctlBurst == 1 ? C_ACCENT : C_BTN, C_LINE, 14));
            ctlBurstBtn.setTextColor(ctlBurst == 1 ? 0xFFFFFFFF : (ctlBurst == 0 ? 0xFFFFC2C2 : C_TXT));
        }
        if (ctlSpeedVal != null) {
            int p = (int) Math.round(ctlSpeed * 100) - 30;
            if (p < 0) p = 0;
            if (ctlSpeedBar.getProgress() != p) ctlSpeedBar.setProgress(p);
            ctlSpeedVal.setText(String.format("%.2fx", ctlSpeed));
        }
        if (ctlRateVal != null) {
            // 以前 ctlRate < 0（跟全局）时直接 setEnabled(false)，滑动条划不动，
            // 而想启用得先去点右边那个「跟随全局」按钮 —— 语义反了。现在滑动条永远可拖，
            // 拖一下就自动脱离「跟全局」。
            ctlRateBar.setEnabled(true);
            if (ctlRate < 0) {
                // 2026-10-28：插件那边改了语义 —— 只要这个群不是「只回 @」，频率「跟全局」时
                // 也按 35% 主动接话（FISH_CHAT_RATE_DFT）。这里写清楚，别再让人以为"关了不生效"。
                ctlRateVal.setText(ctlOnlyAt ? "（跟全局 · 只回@）" : "（跟全局 · 按默认 35% 接）");
            } else {
                int p = (int) Math.round(ctlRate * 100);
                if (ctlRateBar.getProgress() != p) ctlRateBar.setProgress(p);
                ctlRateVal.setText(p + "%");
            }
            if (ctlRateClear != null) {
                boolean follow = (ctlRate < 0);
                ctlRateClear.setText(follow ? "跟全局 ✓" : "跟全局");
                ctlRateClear.setTextColor(follow ? 0xFFFFFFFF : C_TXT);
                ctlRateClear.setBackground(roundBg(follow ? C_ACCENT : C_BTN, follow ? 0 : C_LINE, 14));
            }
        }
        if (ctlLenVal != null) ctlLenVal.setText("当前：" + ctlLen + " 字");
        if (ctlPokeBtns != null) {
            for (int i = 0; i < ctlPokeBtns.length; i++) {
                Button b = ctlPokeBtns[i];
                if (b == null) continue;
                boolean on = Math.abs(ctlPoke - CTL_POKE_STEPS[i]) < 0.005;
                b.setBackground(roundBg(on ? C_ACCENT : C_BTN, on ? 0 : C_LINE, 14));
                b.setTextColor(on ? 0xFFFFFFFF : C_TXT);
            }
            if (ctlPokeVal != null) ctlPokeVal.setText(((int) Math.round(ctlPoke * 100)) + "%");
        }
        // 默默发表情：-1 = 没单独设过（用默认 3%）
        if (ctlAmbientBtns != null) {
            for (int i = 0; i < ctlAmbientBtns.length; i++) {
                Button b = ctlAmbientBtns[i];
                if (b == null) continue;
                boolean on = ctlAmbient >= 0 && Math.abs(ctlAmbient - CTL_AMBIENT_STEPS[i]) < 0.005;
                b.setBackground(roundBg(on ? C_ACCENT : C_BTN, on ? 0 : C_LINE, 14));
                b.setTextColor(on ? 0xFFFFFFFF : C_TXT);
            }
        }
        if (ctlAmbientVal != null) {
            ctlAmbientVal.setText(ctlAmbient < 0
                    ? "用默认 3%" : ((int) Math.round(ctlAmbient * 100)) + "%");
        }
        if (ctlAmbientFollow != null) {
            boolean follow = (ctlAmbient < 0);
            ctlAmbientFollow.setText(follow ? "用默认 ✓" : "用默认 3%");
            ctlAmbientFollow.setBackground(roundBg(follow ? C_ACCENT : C_BTN, follow ? 0 : C_LINE, 14));
            ctlAmbientFollow.setTextColor(follow ? 0xFFFFFFFF : C_TXT);
        }
        Button[] lens = {ctlLen14, ctlLen26, ctlLen40};
        int[] vals = {14, 26, 40};
        for (int i = 0; i < lens.length; i++) {
            if (lens[i] == null) continue;
            boolean on = (ctlLen == vals[i]);
            lens[i].setBackground(roundBg(on ? C_ACCENT_D : C_BTN, on ? 0 : C_LINE, 14));
            lens[i].setTextColor(on ? 0xFFDFE7FF : C_TXT);
        }
        // 音色风格高亮
        boolean soft = "diona_soft".equals(ctlVoice);
        if (ctlVoiceOrig != null) {
            ctlVoiceOrig.setBackground(roundBg(!soft ? C_ACCENT : C_BTN, !soft ? 0 : C_LINE, 14));
            ctlVoiceOrig.setTextColor(!soft ? 0xFFFFFFFF : C_TXT);
        }
        if (ctlVoiceSoft != null) {
            ctlVoiceSoft.setBackground(roundBg(soft ? C_ACCENT : C_BTN, soft ? 0 : C_LINE, 14));
            ctlVoiceSoft.setTextColor(soft ? 0xFFFFFFFF : C_TXT);
        }
        if (ctlVoiceVal != null) ctlVoiceVal.setText("当前：" + (soft ? "软音色" : "原音色"));
    }

    void ctlLoadVoice() {
        getJson("/api/voice_style", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) return;
                ctlVoice = o.optString("current", "diona");
                ctlPaint();
            }
        });
    }

    void ctlSetVoice(final String style) {
        if (style.equals(ctlVoice)) return;
        JSONObject body = new JSONObject();
        try {
            body.put("style", style);
        } catch (Exception e) { }
        if (ctlVoiceVal != null) ctlVoiceVal.setText("切换中…");
        postJsonBody("/api/voice_style", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (ctlVoiceVal != null) ctlVoiceVal.setText("切换失败：" + err);
                    toast("切换失败");
                    return;
                }
                boolean ok = o != null && o.optBoolean("ok");
                if (ok) {
                    ctlVoice = style;
                    ctlPaint();
                    toast("已切到" + ("diona_soft".equals(style) ? "软音色" : "原音色") + "，立刻生效");
                } else {
                    if (ctlVoiceVal != null)
                        ctlVoiceVal.setText("切换失败：" + (o == null ? "?" : o.optString("msg")));
                    toast("切换失败");
                }
            }
        });
    }

    void ctlLoadGroups() {
        ctlStatus.setText("拉群列表…");
        getJson("/api/groups", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { ctlStatus.setText("群列表失败：" + err); return; }
                ctlGids.clear();
                ctlNames.clear();
                JSONArray arr = o.optJSONArray("items");
                for (int i = 0; arr != null && i < arr.length(); i++) {
                    JSONObject g = arr.optJSONObject(i);
                    if (g == null) continue;
                    String gid = g.optString("gid");
                    String nm = g.optString("name");
                    ctlGids.add(gid);
                    ctlNames.add("群 " + gid + (nm.isEmpty() ? "" : " · " + nm));
                }
                ArrayAdapter<String> ad = new ArrayAdapter<String>(MainActivity.this,
                        android.R.layout.simple_spinner_item, ctlNames);
                ad.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
                ctlSpinner.setAdapter(ad);
                if (ctlGids.isEmpty()) { ctlStatus.setText("没拿到群列表"); return; }
                ctlChat = "群 " + ctlGids.get(0);
                ctlLoadRules(ctlChat);
            }
        });
    }

    // ================================================== 沉默名单（全局拉黑机器人）
    void blLoad() {
        getJson("/api/bot_blacklist", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { if (blHint != null) blHint.setText("读取失败：" + err); return; }
                JSONArray arr = o == null ? null : o.optJSONArray("items");
                // 列表开着时不覆盖「已解除待移除」的条目
                List<String> keepRemoved = new ArrayList<String>();
                if (blOpen) {
                    for (JSONObject it : blItems) {
                        if (it.optBoolean("removed", false)) keepRemoved.add(it.optString("uid"));
                    }
                }
                blItems.clear();
                for (int i = 0; arr != null && i < arr.length(); i++) {
                    JSONObject it = arr.optJSONObject(i);
                    if (it == null) continue;
                    try {
                        JSONObject c = new JSONObject(it.toString());
                        if (keepRemoved.contains(c.optString("uid"))) c.put("removed", true);
                        blItems.add(c);
                    } catch (Exception e) { }
                }
                blPaint();
            }
        });
    }

    /** 只画：开了才显示，收起时才真正丢掉 removed 的条目（用户要求解除后先留在原位） */
    void blPaint() {
        if (blList == null) return;
        if (blToggle != null) blToggle.setText(blOpen ? "收起名单 ▴" : "展开名单 ▾");
        blList.setVisibility(blOpen ? View.VISIBLE : View.GONE);
        if (!blOpen) return;

        blList.removeAllViews();
        int active = 0;
        for (JSONObject it : blItems) if (!it.optBoolean("removed", false)) active++;

        if (blItems.isEmpty()) {
            blList.addView(text("名单是空的——她目前会正常理会所有号。", 12, C_SUB, false));
            return;
        }
        int pending = blItems.size() - active;
        blList.addView(text("共 " + active + " 个在拉黑中"
                + (pending > 0 ? "，" + pending + " 个待移除" : ""), 11, C_SUB, false));

        for (final JSONObject it : blItems) {
            boolean removed = it.optBoolean("removed", false);
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(0, dp(8), 0, dp(8));

            Button un = btn(removed ? "已解除" : "解除", removed ? 0xFF2C2F35 : 0xFF5A2A2E,
                    removed ? C_SUB : 0xFFFFC9CB, "bl-noop");
            LinearLayout.LayoutParams ulp = new LinearLayout.LayoutParams(dp(84), dp(40));
            ulp.setMargins(dp(8), 0, 0, 0);
            un.setLayoutParams(ulp);
            un.setEnabled(!removed);
            un.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { blRemove(it); }
            });
            row.addView(un);

            String label = it.optString("uid");
            String note = it.optString("note");
            String ts = it.optString("ts");
            if (note.length() > 0) label += "  " + note;
            if (ts.length() > 0) label += "  · " + ts;
            TextView tv = text(label, 12, removed ? C_SUB : C_TXT, false);
            tv.setTextSize(12);
            if (removed) tv.setPaintFlags(tv.getPaintFlags() | android.graphics.Paint.STRIKE_THRU_TEXT_FLAG);
            LinearLayout.LayoutParams tlp = new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
            tv.setLayoutParams(tlp);
            row.addView(tv);

            blList.addView(row);
        }
    }

    void blToggleOpen() {
        if (blOpen) {
            // 收起 = 真正移除那些已解除的
            int before = blItems.size();
            List<JSONObject> keep = new ArrayList<JSONObject>();
            for (JSONObject it : blItems) if (!it.optBoolean("removed", false)) keep.add(it);
            blItems.clear();
            blItems.addAll(keep);
            if (blHint != null && before != blItems.size())
                blHint.setText("已解除 " + (before - blItems.size()) + " 个");
        }
        blOpen = !blOpen;
        blPaint();
    }

    void blAdd() {
        String uid = blInput == null ? "" : blInput.getText().toString().trim();
        if (uid.length() == 0) { if (blHint != null) blHint.setText("先填 QQ 号"); return; }
        for (char ch : uid.toCharArray()) {
            if (!Character.isDigit(ch)) { if (blHint != null) blHint.setText("QQ 号只能是数字"); return; }
        }
        for (JSONObject it : blItems) {
            if (uid.equals(it.optString("uid")) && !it.optBoolean("removed", false)) {
                if (blHint != null) blHint.setText("这个号已经在名单里了");
                return;
            }
        }
        String note = blNote == null ? "" : blNote.getText().toString().trim();
        blInput.setText("");
        if (blNote != null) blNote.setText("");
        if (blHint != null) blHint.setText("正在拉黑 " + uid + " …");

        JSONObject body = new JSONObject();
        try { body.put("action", "add"); body.put("uid", uid); if (note.length() > 0) body.put("note", note); }
        catch (Exception e) { }
        postJsonBody("/api/bot_blacklist", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { if (blHint != null) blHint.setText("添加失败：" + err); return; }
                boolean ok = o != null && o.optBoolean("ok");
                if (blHint != null)
                    blHint.setText(ok ? o.optString("msg", "已拉黑") : o.optString("msg", "添加失败"));
                if (ok) blLoad();
            }
        });
    }

    void blRemove(final JSONObject item) {
        if (item.optBoolean("removed", false)) return;
        try { item.put("removed", true); } catch (Exception e) { }
        blPaint();
        if (blHint != null) blHint.setText("已解除 " + item.optString("uid") + "（收起列表后才从名单里消失）");

        JSONObject body = new JSONObject();
        try { body.put("action", "remove"); body.put("uid", item.optString("uid")); }
        catch (Exception e) { }
        postJsonBody("/api/bot_blacklist", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (o != null && !o.optBoolean("ok")) {
                    if (blHint != null) blHint.setText("解除失败：" + o.optString("msg", "?"));
                }
            }
        });
    }

    void ctlLoadRules(String chat) {
        ctlStatus.setText("读取 " + chat + " …");
        try {
            getJson("/api/botctl?chat=" + URLEncoder.encode(chat, "UTF-8"), new JsonCb() {
                public void on(JSONObject o, String err) {
                    if (err != null) { ctlStatus.setText("读取失败：" + err); return; }
                    JSONObject r = o.optJSONObject("rules");
                    if (r != null) {
                        ctlOnlyAt = r.optBoolean("only_at", false);
                        ctlLen = r.optInt("max_len", 0);
                        if (ctlLen <= 0) ctlLen = 26;
                        ctlSpeed = r.optDouble("speed", 1.0);
                        if (ctlSpeed <= 0) ctlSpeed = 1.0;
                        ctlRate = r.optDouble("chat_rate", -1.0);
                        // 戳一戳概率：吸附到最近的挡位（手改过 json 也不怕没按钮选中）
                        ctlPoke = ctlSnapPoke(r.optDouble("poke_rate", 1.0));
                        // 默默发表情：-1 = 没单独设过（用插件默认 3%）
                        ctlAmbient = ctlSnapAmbient(r.has("ambient") ? r.optDouble("ambient", -1.0) : -1.0);
                        // C1：ai 可能是 null（跟随全局）、true、false
                        if (!r.has("ai") || r.isNull("ai")) ctlAi = -1;
                        else ctlAi = r.optBoolean("ai", true) ? 1 : 0;
                        // 刷屏惊讶（2026-10-28）同款三态
                        if (!r.has("burst") || r.isNull("burst")) ctlBurst = -1;
                        else ctlBurst = r.optBoolean("burst", true) ? 1 : 0;
                    }
                    ctlPaint();
                    ctlStatus.setText("已载入 " + ctlChat);
                }
            });
        } catch (Exception e) {
            ctlStatus.setText("群名编码失败");
        }
    }

    void ctlSave() {
        if (ctlChat == null) { toast("先选一个群"); return; }
        ctlStatus.setText("保存中…");
        JSONObject body = new JSONObject();
        try {
            body.put("chat", ctlChat);
            body.put("only_at", ctlOnlyAt);
            body.put("max_len", ctlLen);
            body.put("speed", Math.round(ctlSpeed * 100) / 100.0);
            body.put("chat_rate", Math.round(ctlRate * 100) / 100.0);
            if (ctlAi < 0) body.put("ai", JSONObject.NULL);
            else body.put("ai", ctlAi == 1);
            if (ctlBurst < 0) body.put("burst", JSONObject.NULL);
            else body.put("burst", ctlBurst == 1);
            body.put("poke_rate", Math.round(ctlPoke * 100) / 100.0);
            body.put("ambient", ctlAmbient < 0 ? -1.0 : Math.round(ctlAmbient * 100) / 100.0);
        } catch (Exception e) { }
        postJsonBody("/api/botctl", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { ctlStatus.setText("保存失败：" + err); return; }
                boolean ok = o != null && o.optBoolean("ok");
                ctlStatus.setText(ok
                        ? "已保存：" + ctlChat + " → 仅@" + (ctlOnlyAt ? "开" : "关")
                          + "，长度 " + ctlLen + "，速度 " + String.format("%.2fx", ctlSpeed)
                          + "，频率 " + (ctlRate < 0 ? "跟全局" : ((int) Math.round(ctlRate * 100)) + "%")
                        : ("保存失败：" + (o == null ? "?" : o.optString("msg"))));
                toast(ok ? "已保存" : "保存失败");
            }
        });
    }

    void postJsonBody(final String path, final JSONObject body, final JsonCb cb) {
        pool.execute(new Runnable() {
            public void run() {
                String err = null;
                JSONObject obj = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api(path)).openConnection();
                    c.setDoOutput(true);
                    c.setRequestMethod("POST");
                    c.setRequestProperty("Content-Type", "application/json");
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(60000);
                    c.getOutputStream().write(body.toString().getBytes("UTF-8"));
                    int code = c.getResponseCode();
                    InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
                    String txt = read(is);
                    if (code == 401) err = "token 无效";
                    else if (code >= 400) err = "服务器返回 " + code;
                    else obj = new JSONObject(txt);
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final JSONObject fo = obj;
                final String fe = err;
                ui.post(new Runnable() { public void run() { cb.on(fo, fe); } });
            }
        });
    }

    LinearLayout buildLogs() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);

        LinearLayout tabs = new LinearLayout(this);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        logTabMsg = logTab("会话", true);
        logTabRun = logTab("运行日志", false);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        lp.setMargins(0, 0, dp(8), dp(10));
        logTabMsg.setLayoutParams(lp);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        lp2.setMargins(0, 0, 0, dp(10));
        logTabRun.setLayoutParams(lp2);
        tabs.addView(logTabMsg);
        tabs.addView(logTabRun);
        page.addView(tabs);

        // 会话列表
        convCard = card();
        convBox = new LinearLayout(this);
        convBox.setOrientation(LinearLayout.VERTICAL);
        convCard.addView(convBox);
        page.addView(convCard);

        // 聊天视图（默认隐藏）
        chatCardView = card();
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        TextView back = text("‹ 返回", 15, C_ACCENT, true);
        back.setPadding(0, dp(4), dp(14), dp(8));
        back.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { closeChat(); }
        });
        head.addView(back);
        TextView title = text("", 14, C_TXT, true);
        title.setTag("chattitle");
        title.setPadding(0, dp(4), 0, dp(8));
        head.addView(title);
        chatCardView.addView(head);
        chatBox = new LinearLayout(this);
        chatBox.setOrientation(LinearLayout.VERTICAL);
        chatBox.setPadding(dp(2), dp(2), dp(2), dp(2));
        chatScroll = new InnerScrollView(this);
        chatScroll.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, chatHeight()));
        chatScroll.setVerticalScrollBarEnabled(true);
        chatScroll.setScrollbarFadingEnabled(false);
        chatScroll.setFillViewport(false);
        chatScroll.addView(chatBox, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        // 手指一碰就停止自动贴底（不然用户往上翻的时候会被拽回去）
        chatScroll.setOnTouchListener(new View.OnTouchListener() {
            public boolean onTouch(View v, android.view.MotionEvent e) {
                if (e.getAction() == android.view.MotionEvent.ACTION_DOWN) stickBottom = false;
                return false;
            }
        });
        // 消息一多，高度要好几帧才算准，定时滚会滚在半路；这里按布局事件滚，
        // stickBottom 为 false（用户自己翻上去了）就什么都不做。
        chatBox.getViewTreeObserver().addOnGlobalLayoutListener(
                new android.view.ViewTreeObserver.OnGlobalLayoutListener() {
                    public void onGlobalLayout() {
                        if (stickBottom && chatScroll != null && chatBox != null) {
                            chatScroll.scrollTo(0, chatBox.getHeight());
                        }
                    }
                });
        chatCardView.addView(chatScroll);

        // 快捷语
        LinearLayout quickRow = new LinearLayout(this);
        quickRow.setOrientation(LinearLayout.HORIZONTAL);
        quickRow.setPadding(0, dp(8), 0, 0);
        for (int qi = 0; qi < QUICK.length; qi++) {
            final String q = QUICK[qi];
            TextView chip = text(q, 11, C_SUB, false);
            chip.setPadding(dp(10), dp(6), dp(10), dp(6));
            chip.setBackground(roundBg(0xFF22252A, C_LINE, 14));
            LinearLayout.LayoutParams qlp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            qlp.setMargins(0, 0, dp(6), 0);
            chip.setLayoutParams(qlp);
            chip.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { chatInput.setText(q); doSendChat(); }
            });
            quickRow.addView(chip);
        }
        TextView vt = text("语音", 11, C_SUB, false);
        vt.setPadding(dp(10), dp(6), dp(10), dp(6));
        vt.setBackground(roundBg(0xFF22252A, C_LINE, 14));
        vt.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { toggleVoiceMode(); }
        });
        voiceToggle = vt;
        LinearLayout.LayoutParams vtlp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        vtlp.setMargins(dp(4), 0, 0, 0);
        vt.setLayoutParams(vtlp);
        quickRow.addView(vt);
        chatCardView.addView(quickRow);

        // 引用条 / @ 芯片（有内容才显示）
        composeBar = new LinearLayout(this);
        composeBar.setOrientation(LinearLayout.VERTICAL);
        composeBar.setPadding(0, dp(8), 0, 0);
        composeBar.setVisibility(View.GONE);
        chatCardView.addView(composeBar);

        // 主动发送：输入框 + 发送按钮
        LinearLayout sendRow = new LinearLayout(this);
        sendRow.setOrientation(LinearLayout.HORIZONTAL);
        sendRow.setGravity(Gravity.CENTER_VERTICAL);
        sendRow.setPadding(0, dp(8), 0, 0);

        // 2026-10-30 用户：「按钮把输入框挤没了」→ 三个工具钮改成**浮在输入框右边**的小图钉，
        // 输入框独占整行宽度，「发送」留在外面。
        FrameLayout inputWrap = new FrameLayout(this);
        LinearLayout.LayoutParams wrapLp = new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        wrapLp.setMargins(0, 0, dp(8), 0);
        inputWrap.setLayoutParams(wrapLp);

        chatInput = new EditText(this);
        chatInput.setHint("以她的身份发一句话…");
        chatInput.setHintTextColor(C_SUB);
        chatInput.setTextColor(C_TXT);
        chatInput.setTextSize(14);
        chatInput.setBackground(roundBg(0xFF22252A, C_LINE, 12));
        // 右边留出悬浮按钮的宽度，字不会被压在按钮底下
        chatInput.setPadding(dp(12), dp(10), dp(118), dp(10));
        chatInput.setMaxLines(3);
        inputWrap.addView(chatInput, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        LinearLayout fbar = new LinearLayout(this);
        fbar.setOrientation(LinearLayout.HORIZONTAL);
        fbar.setGravity(Gravity.CENTER_VERTICAL);

        TextView stkEntry = text("😀", 14, C_SUB, false);
        stkEntry.setGravity(Gravity.CENTER);
        stkEntry.setBackground(roundBg(C_BTN, C_LINE, 10));
        stkEntry.setLayoutParams(new LinearLayout.LayoutParams(dp(32), dp(32)));
        stkEntry.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { showStickerPicker(); }
        });
        fbar.addView(stkEntry);

        TextView slangEntry = text("🗣", 14, C_SUB, false);
        slangEntry.setGravity(Gravity.CENTER);
        slangEntry.setBackground(roundBg(C_BTN, C_LINE, 10));
        LinearLayout.LayoutParams glp2 = new LinearLayout.LayoutParams(dp(32), dp(32));
        glp2.setMargins(dp(6), 0, 0, 0);
        slangEntry.setLayoutParams(glp2);
        slangEntry.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { showSlangEditor(); }
        });
        fbar.addView(slangEntry);

        TextView atBtn = text("＠", 14, C_SUB, true);
        atBtn.setGravity(Gravity.CENTER);
        atBtn.setBackground(roundBg(C_BTN, C_LINE, 10));
        LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(dp(32), dp(32));
        alp.setMargins(dp(6), 0, 0, 0);
        atBtn.setLayoutParams(alp);
        atBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { showMemberPicker(""); }
        });
        fbar.addView(atBtn);

        FrameLayout.LayoutParams barLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        barLp.setMargins(0, 0, dp(6), 0);
        inputWrap.addView(fbar, barLp);

        sendRow.addView(inputWrap);
        Button sendBtn = btn("发送", C_ACCENT, 0xFFFFFFFF, "sendchat");
        LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(dp(76), dp(46));
        sendBtn.setLayoutParams(blp);
        sendRow.addView(sendBtn);
        chatCardView.addView(sendRow);

        chatCardView.setVisibility(View.GONE);
        page.addView(chatCardView);

        // 运行日志
        LinearLayout runCard = card();
        runBox = text("加载中…", 11, 0xFFC7CCD4, false);
        runBox.setTypeface(Typeface.MONOSPACE);
        runBox.setLineSpacing(dp(3), 1f);
        runBox.setTextIsSelectable(true);
        runScroll = new InnerScrollView(this);
        runScroll.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, chatHeight()));
        runScroll.setVerticalScrollBarEnabled(true);
        runScroll.setScrollbarFadingEnabled(false);
        runScroll.addView(runBox, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        runCard.addView(runScroll);
        runCard.setVisibility(View.GONE);
        runCard.setTag("runcard");
        page.addView(runCard);
        return page;
    }

    TextView logTab(String name, final boolean msg) {
        TextView t = new TextView(this);
        t.setText(name);
        t.setTextSize(14);
        t.setGravity(Gravity.CENTER);
        t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setPadding(0, dp(12), 0, dp(12));
        t.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                logMsg = msg;
                paintTab(logTabMsg, msg);
                paintTab(logTabRun, !msg);
                if (msg) { showConvList(); loadConversations(); startChatPoll(); }
                else { if (convCard != null) convCard.setVisibility(View.GONE);
                       if (chatCardView != null) chatCardView.setVisibility(View.GONE);
                       View rc = findRunCard(); if (rc != null) rc.setVisibility(View.VISIBLE);
                       loadLogs(); stopChatPoll(); }
            }
        });
        return t;
    }

    // ================================================== 小工具
    int statusBarHeight() {
        int id = getResources().getIdentifier("status_bar_height", "dimen", "android");
        return id > 0 ? getResources().getDimensionPixelSize(id) : dp(24);
    }

    int chatHeight() {
        int screen = getResources().getDisplayMetrics().heightPixels;
        int h = (int) (screen * 0.46f);        // 屏幕高度的 46%（openChat 后还会按真实剩余高度再校一次）
        int min = dp(280);
        int max = dp(540);
        if (h < min) h = min;
        if (h > max) h = max;
        return h;
    }

    /**
     * 按「屏幕里真正还剩多少地方」定聊天区高度。
     *
     * 以前固定 52% 屏高，加上标题/Tab/快捷语/输入框就超出屏幕，每次都得先往上划一下
     * 才能点到输入框。这里量一次卡片的位置，把多出来的高度从消息区里扣掉。
     */
    void fitChatHeight() {
        if (chatCardView == null || chatScroll == null || rootScroll == null) return;
        if (chatCardView.getVisibility() != View.VISIBLE) return;
        rootScroll.post(new Runnable() {
            public void run() {
                if (chatCardView == null || chatScroll == null) return;
                if (chatCardView.getVisibility() != View.VISIBLE) return;
                int viewport = rootScroll.getHeight();
                int cardH = chatCardView.getHeight();
                int scrollH = chatScroll.getHeight();
                if (viewport <= 0 || cardH <= 0 || scrollH <= 0) return;

                int top = 0;
                View v = chatCardView;
                while (v != null && v != rootScroll) {
                    top += v.getTop();
                    android.view.ViewParent p = v.getParent();
                    v = (p instanceof View) ? (View) p : null;
                }

                int chrome = cardH - scrollH;                 // 卡片里除消息区以外的部分
                int target = viewport - top - chrome - dp(10);
                int minH = dp(200);
                int maxH = (int) (getResources().getDisplayMetrics().heightPixels * 0.62f);
                if (target > maxH) target = maxH;
                if (target < minH) target = minH;
                if (Math.abs(target - scrollH) < dp(6)) return;   // 已经在位，别来回跳

                ViewGroup.LayoutParams lp = chatScroll.getLayoutParams();
                lp.height = target;
                chatScroll.setLayoutParams(lp);
                if (stickBottom) scrollToBottom();
            }
        });
    }

    // ================================================== 她的 QQ
    static final int C_QQ_BG = 0xFF1B2230;

    LinearLayout buildQQProfile() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setVisibility(View.GONE);

        // ---------- 资料卡 ----------
        LinearLayout card = card();
        card.addView(text("她的 QQ 主页", 16, C_TXT, true));

        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.TOP);
        head.setPadding(0, dp(12), 0, 0);

        qqAva = new ImageView(this);
        LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(dp(84), dp(84));
        alp.setMargins(0, 0, dp(14), 0);
        qqAva.setLayoutParams(alp);
        qqAva.setScaleType(ImageView.ScaleType.CENTER_CROP);
        qqAva.setBackground(roundBg(C_BTN, C_LINE, 42));
        head.addView(qqAva);

        LinearLayout info = new LinearLayout(this);
        info.setOrientation(LinearLayout.VERTICAL);
        qqName = text("读取中…", 19, C_TXT, true);
        info.addView(qqName);
        qqNum = text("", 12, C_SUB, false);
        qqNum.setPadding(0, dp(3), 0, 0);
        info.addView(qqNum);
        qqSign = text("", 13, C_TXT, false);
        qqSign.setPadding(0, dp(8), 0, 0);
        info.addView(qqSign);
        qqStat = text("", 11, C_SUB, false);
        qqStat.setPadding(0, dp(6), 0, 0);
        info.addView(qqStat);
        LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        info.setLayoutParams(ilp);
        head.addView(info);
        card.addView(head);

        LinearLayout btns = new LinearLayout(this);
        btns.setOrientation(LinearLayout.HORIZONTAL);
        btns.setPadding(0, dp(14), 0, 0);
        btns.addView(qqBtn("换头像", true, new View.OnClickListener() {
            public void onClick(View v) { qqPickAvatar(); }
        }));
        btns.addView(qqBtn("刷新", false, new View.OnClickListener() {
            public void onClick(View v) { loadQQProfile(); }
        }));
        card.addView(btns);
        page.addView(card);

        // ---------- 改昵称 / 签名 ----------
        LinearLayout edit = card();
        edit.addView(text("改昵称 / 签名", 16, C_TXT, true));
        edit.addView(qqHint("改完立刻生效，不用重启。签名最多 100 字、昵称最多 24 字。"));
        edit.addView(qqHint("注意：这个协议端改签名会同时往说说里发一条。"));

        qqNickIn = qqInput("新昵称，留空=不改", false);
        edit.addView(qqNickIn);
        qqSignIn = qqInput("新签名，留空=不改", true);
        edit.addView(qqSignIn);

        LinearLayout saveRow = new LinearLayout(this);
        saveRow.setOrientation(LinearLayout.HORIZONTAL);
        saveRow.setPadding(0, dp(10), 0, 0);
        saveRow.addView(qqBtn("保存", true, new View.OnClickListener() {
            public void onClick(View v) { qqSaveProfile(); }
        }));
        edit.addView(saveRow);
        page.addView(edit);

        // ---------- 说说 ----------
        LinearLayout qz = card();
        qz.addView(text("说说", 16, C_TXT, true));
        qz.addView(qqHint("发一条说说（会显示在她的 QQ 空间/个性签名）。"));

        qqPostIn = qqInput("想说什么…（最多 1500 字）", true);
        qz.addView(qqPostIn);

        qqImgStrip = new LinearLayout(this);
        qqImgStrip.setOrientation(LinearLayout.HORIZONTAL);
        qqImgStrip.setPadding(0, dp(8), 0, 0);
        qz.addView(qqImgStrip);

        LinearLayout postRow = new LinearLayout(this);
        postRow.setOrientation(LinearLayout.HORIZONTAL);
        postRow.setPadding(0, dp(10), 0, 0);
        postRow.addView(qqBtn("选图", false, new View.OnClickListener() {
            public void onClick(View v) { qqPickPostImages(); }
        }));
        postRow.addView(qqBtn("发布说说", true, new View.OnClickListener() {
            public void onClick(View v) { qqPost(); }
        }));
        postRow.addView(qqBtn("刷新列表", false, new View.OnClickListener() {
            public void onClick(View v) { loadQZone(); }
        }));
        qz.addView(postRow);

        qqListHint = text("", 11, C_SUB, false);
        qqListHint.setPadding(0, dp(10), 0, 0);
        qz.addView(qqListHint);

        qqList = new LinearLayout(this);
        qqList.setOrientation(LinearLayout.VERTICAL);
        qqList.setPadding(0, dp(8), 0, 0);
        qz.addView(qqList);
        page.addView(qz);

        return page;
    }

    Button qqBtn(String label, boolean primary, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(13);
        b.setTextColor(primary ? 0xFFFFFFFF : C_TXT);
        b.setBackground(roundBg(primary ? C_ACCENT : C_BTN, primary ? 0 : C_LINE, 10));
        b.setPadding(dp(18), dp(14), dp(18), dp(14));   // 触摸区放大，别让人点不到
        b.setMinHeight(dp(48));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, dp(8), 0);
        b.setLayoutParams(lp);
        b.setOnClickListener(l);
        return b;
    }

    TextView qqHint(String t) {
        TextView v = text(t, 11, C_SUB, false);
        v.setPadding(0, dp(5), 0, 0);
        return v;
    }

    EditText qqInput(String hint, boolean multi) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setHintTextColor(C_SUB);
        e.setTextColor(C_TXT);
        e.setTextSize(13);
        e.setBackground(roundBg(C_BTN, C_LINE, 10));
        e.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, dp(8), 0, 0);
        e.setLayoutParams(lp);
        if (multi) {
            e.setInputType(android.text.InputType.TYPE_CLASS_TEXT
                    | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
            e.setMinLines(2);
            e.setGravity(Gravity.TOP | Gravity.START);
        } else {
            e.setSingleLine(true);
        }
        return e;
    }

    void loadQQProfile() {
        getJson("/api/profile", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (o == null) { if (qqStat != null) qqStat.setText("读取失败：" + err); return; }
                String nick = o.optString("nickname", "");
                String sign = o.optString("signature", "");
                String uin = o.optString("uin", "");
                int lv = o.optInt("level", 0);
                if (qqName != null) qqName.setText(nick.length() == 0 ? "(没读到昵称)" : nick);
                if (qqNum != null) qqNum.setText("QQ " + uin + (lv > 0 ? ("　Lv." + lv) : ""));
                if (qqSign != null)
                    qqSign.setText(sign.length() == 0 ? "（还没写签名）" : ("「" + sign + "」"));
                String warn = o.optString("warn", "");
                if (qqStat != null) qqStat.setText(warn.length() > 0 ? warn : "同步正常");
                String b64 = o.optString("avatar_b64", "");
                if (b64.length() > 0 && qqAva != null && !b64.equals(qqAvaB64)) {
                    qqAvaB64 = b64;
                    try {
                        byte[] raw = android.util.Base64.decode(b64, android.util.Base64.DEFAULT);
                        Bitmap bm = BitmapFactory.decodeByteArray(raw, 0, raw.length);
                        if (bm != null) qqAva.setImageBitmap(circleCrop(bm));
                    } catch (Throwable t) { }
                }
            }
        });
    }

    /** 头像裁成圆的（ImageView 的圆角背景不会裁图，得自己做） */
    Bitmap circleCrop(Bitmap src) {
        try {
            int w = src.getWidth(), h = src.getHeight();
            int d = Math.min(w, h);
            Bitmap out = Bitmap.createBitmap(d, d, Bitmap.Config.ARGB_8888);
            Canvas cv = new Canvas(out);
            Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
            p.setShader(new BitmapShader(Bitmap.createBitmap(src,
                    (w - d) / 2, (h - d) / 2, d, d), Shader.TileMode.CLAMP, Shader.TileMode.CLAMP));
            cv.drawCircle(d / 2f, d / 2f, d / 2f, p);
            return out;
        } catch (Throwable t) {
            return src;
        }
    }

    void qqSaveProfile() {
        String nick = qqNickIn == null ? "" : qqNickIn.getText().toString().trim();
        String sign = qqSignIn == null ? "" : qqSignIn.getText().toString().trim();
        if (nick.length() == 0 && sign.length() == 0) {
            if (qqStat != null) qqStat.setText("昵称和签名都空着，没东西可改");
            return;
        }
        try {
            JSONObject body = new JSONObject();
            if (nick.length() > 0) body.put("nickname", nick);
            if (sign.length() > 0) body.put("signature", sign);
            if (qqStat != null) qqStat.setText("保存中…");
            postJsonBody("/api/profile/save", body, new JsonCb() {
                public void on(JSONObject o, String err) {
                    if (o == null) {
                        if (qqStat != null) qqStat.setText("保存失败：" + err);
                        return;
                    }
                    if (qqStat != null) qqStat.setText(o.optString("msg", "改好了"));
                    if (o.optBoolean("ok", false)) {
                        if (qqNickIn != null) qqNickIn.setText("");
                        if (qqSignIn != null) qqSignIn.setText("");
                        qqAvaB64 = "";
                    }
                    loadQQProfile();
                    loadQZone();
                }
            });
        } catch (Exception e) {
            toast("保存失败：" + e.getMessage());
        }
    }

    void qqPickPostImages() {
        if (qqPostImgs.size() >= 9) { toast("最多 9 张"); return; }
        Intent i = new Intent(Intent.ACTION_GET_CONTENT);
        i.setType("image/*");
        i.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        try {
            startActivityForResult(Intent.createChooser(i, "选几张图一起发"), REQ_PICK_QIMG);
        } catch (Exception e) {
            toast("打不开相册：" + e.getMessage());
        }
    }

    void qqOnPickPostImages(int res, Intent data) {
        if (res != RESULT_OK || data == null) return;
        List<android.net.Uri> uris = new ArrayList<android.net.Uri>();
        try {
            if (data.getClipData() != null) {
                android.content.ClipData cd = data.getClipData();
                for (int k = 0; k < cd.getItemCount(); k++) uris.add(cd.getItemAt(k).getUri());
            } else if (data.getData() != null) {
                uris.add(data.getData());
            }
        } catch (Throwable t) { }
        int added = 0;
        for (android.net.Uri u : uris) {
            if (qqPostImgs.size() >= 9) break;
            try {
                InputStream in = getContentResolver().openInputStream(u);
                byte[] raw = readAllBytes(in);
                if (in != null) in.close();
                byte[] jpg = shrinkJpeg(raw, 1280);
                if (jpg == null) jpg = raw;
                if (jpg == null || jpg.length == 0 || jpg.length > 8 * 1024 * 1024) continue;
                qqPostImgs.add(jpg);
                added++;
            } catch (Exception e) { }
        }
        qqRepaintImgStrip();
        toast(added > 0 ? ("已选 " + qqPostImgs.size() + " 张") : "没读到图片");
    }

    void qqRepaintImgStrip() {
        if (qqImgStrip == null) return;
        qqImgStrip.removeAllViews();
        for (int i = 0; i < qqPostImgs.size(); i++) {
            final int idx = i;
            ImageView iv = new ImageView(this);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(60), dp(60));
            lp.setMargins(0, 0, dp(8), 0);
            iv.setLayoutParams(lp);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            iv.setBackground(roundBg(C_BTN, C_LINE, 8));
            try {
                Bitmap bm = BitmapFactory.decodeByteArray(qqPostImgs.get(i), 0, qqPostImgs.get(i).length);
                if (bm != null) iv.setImageBitmap(bm);
            } catch (Throwable t) { }
            iv.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    if (idx >= 0 && idx < qqPostImgs.size()) {
                        qqPostImgs.remove(idx);
                        qqRepaintImgStrip();
                    }
                }
            });
            qqImgStrip.addView(iv);
        }
    }

    void qqPickAvatar() {
        Intent i = new Intent(Intent.ACTION_GET_CONTENT);
        i.setType("image/*");
        try {
            startActivityForResult(Intent.createChooser(i, "选一张新头像"), REQ_PICK_AVA);
        } catch (Exception e) {
            toast("打不开相册：" + e.getMessage());
        }
    }

    void qqOnPickAvatar(int res, Intent data) {
        if (res != RESULT_OK || data == null || data.getData() == null) return;
        try {
            InputStream in = getContentResolver().openInputStream(data.getData());
            byte[] raw = readAllBytes(in);
            if (in != null) in.close();
            if (raw == null || raw.length == 0) { toast("读不到图"); return; }
            byte[] small = shrinkJpeg(raw, 640);          // 头像不用太大
            if (small != null) raw = small;
            if (raw.length > 6 * 1024 * 1024) { toast("图太大（>6MB）"); return; }
            if (qqStat != null) qqStat.setText("上传头像中…");
            JSONObject body = new JSONObject();
            body.put("image", android.util.Base64.encodeToString(raw, android.util.Base64.NO_WRAP));
            postJsonBody("/api/profile/avatar", body, new JsonCb() {
                public void on(JSONObject o, String err) {
                    if (o == null) {
                        if (qqStat != null) qqStat.setText("换头像失败：" + err);
                        return;
                    }
                    if (qqStat != null) qqStat.setText(o.optString("msg", ""));
                    if (o.optBoolean("ok", false)) { qqAvaB64 = ""; loadQQProfile(); }
                }
            });
        } catch (Exception e) {
            toast("换头像失败：" + e.getMessage());
        }
    }

    void qqPost() {
        if (qqPostIn == null) return;
        String c = qqPostIn.getText().toString().trim();
        if (c.length() == 0 && qqPostImgs.isEmpty()) { toast("内容和图片都空着"); return; }
        try {
            JSONObject body = new JSONObject();
            body.put("content", c);
            if (!qqPostImgs.isEmpty()) {
                JSONArray arr = new JSONArray();
                for (byte[] b : qqPostImgs)
                    arr.put(android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP));
                body.put("images", arr);
            }
            postJsonBody("/api/qzone/post", body, new JsonCb() {
                public void on(JSONObject o, String err) {
                    if (o == null) { toast("发说说失败：" + err); return; }
                    if (o.optBoolean("ok", false)) {
                        if (qqPostIn != null) qqPostIn.setText("");
                        qqPostImgs.clear();
                        qqRepaintImgStrip();
                        toast(o.optString("msg", "发出去了"));
                        loadQZone();
                        loadQQProfile();
                    } else {
                        toast(o.optString("msg", "发说说失败"));
                    }
                }
            });
        } catch (Exception e) {
            toast("发说说失败：" + e.getMessage());
        }
    }

    void qqDel(final String tid) {
        try {
            JSONObject body = new JSONObject();
            body.put("tid", tid);
            postJsonBody("/api/qzone/del", body, new JsonCb() {
                public void on(JSONObject o, String err) {
                    if (o == null) { toast("删除失败：" + err); return; }
                    toast(o.optString("msg", "删掉了"));
                    loadQZone();
                    loadQQProfile();
                }
            });
        } catch (Exception e) {
            toast("删除失败：" + e.getMessage());
        }
    }

    void loadQZone() {
        getJson("/api/qzone", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (qqList == null) return;
                qqList.removeAllViews();
                if (o == null) {
                    if (qqListHint != null) qqListHint.setText("读取说说失败：" + err);
                    return;
                }
                if (!o.optBoolean("ok", false)) {
                    if (qqListHint != null) qqListHint.setText(o.optString("msg", "读取失败"));
                    return;
                }
                JSONArray arr = o.optJSONArray("items");
                int total = o.optInt("total", 0);
                if (qqListHint != null)
                    qqListHint.setText(arr == null || arr.length() == 0
                            ? "还没有说说。" : ("共 " + total + " 条"));
                if (arr == null) return;
                for (int i = 0; i < arr.length(); i++) {
                    JSONObject it = arr.optJSONObject(i);
                    if (it == null) continue;
                    final String tid = it.optString("tid", "");
                    String content = it.optString("content", "");
                    long ts = it.optLong("time", 0);
                    int cm = it.optInt("comments", 0);

                    LinearLayout row = new LinearLayout(MainActivity.this);
                    row.setOrientation(LinearLayout.VERTICAL);
                    row.setBackground(roundBg(C_BTN, 0, 10));
                    row.setPadding(dp(12), dp(10), dp(12), dp(10));
                    LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(
                            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                    rlp.setMargins(0, dp(6), 0, 0);
                    row.setLayoutParams(rlp);

                    String when = "";
                    try {
                        java.text.SimpleDateFormat f =
                                new java.text.SimpleDateFormat("MM-dd HH:mm");
                        when = f.format(new java.util.Date(ts * 1000L));
                    } catch (Throwable t) { }
                    String meta = when + (cm > 0 ? ("　" + cm + " 条评论") : "");
                    row.addView(text(meta, 10, C_SUB, false));

                    TextView c = text(content, 13, C_TXT, false);
                    c.setPadding(0, dp(5), 0, 0);
                    row.addView(c);

                    LinearLayout acts = new LinearLayout(MainActivity.this);
                    acts.setOrientation(LinearLayout.HORIZONTAL);
                    acts.setPadding(0, dp(6), 0, 0);
                    // 之前按钮只有 ~102px 高，手指容易点到外面（那次"删不掉"就是这个）
                    // 现在给足内边距，并且宽度撑满，随便点都中
                    TextView del = text("删除", 13, 0xFFFF8080, true);
                    del.setGravity(Gravity.CENTER);
                    del.setPadding(dp(14), dp(14), dp(14), dp(14));
                    del.setBackground(roundBg(C_CARD, C_LINE, 10));
                    del.setMinHeight(dp(52));
                    LinearLayout.LayoutParams dlp2 = new LinearLayout.LayoutParams(
                            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                    del.setLayoutParams(dlp2);
                    del.setOnClickListener(new View.OnClickListener() {
                        public void onClick(View v) { qqDel(tid); }
                    });
                    acts.addView(del);
                    row.addView(acts);

                    qqList.addView(row);
                }
            }
        });
    }

    int dp(int v) {
        return (int) (v * getResources().getDisplayMetrics().density + 0.5f);
    }

    TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    LinearLayout card() {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setBackground(roundBg(C_CARD, C_LINE, 16));
        c.setPadding(dp(14), dp(14), dp(14), dp(14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, 0, dp(12));
        c.setLayoutParams(lp);
        return c;
    }

    GradientDrawable roundBg(int fill, int stroke, int radius) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill);
        g.setCornerRadius(dp(radius));
        if (stroke != 0) g.setStroke(dp(1), stroke);
        return g;
    }

    TextView addRow(LinearLayout parent, String key) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(3), 0, dp(3));
        TextView k = text(key, 13, C_SUB, false);
        k.setWidth(dp(84));
        TextView v = text("–", 13, C_TXT, false);
        row.addView(k);
        row.addView(v);
        parent.addView(row);
        return v;
    }

    LinearLayout buttonRow(Button a, Button c) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(54), 1f);
        lp.setMargins(0, dp(10), dp(5), 0);
        a.setLayoutParams(lp);
        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(0, dp(54), 1f);
        lp2.setMargins(dp(5), dp(10), 0, 0);
        c.setLayoutParams(lp2);
        row.addView(a);
        row.addView(c);
        return row;
    }

    Button btn(String label, int bg, int fg, final String action) {
        Button b = new Button(this);
        b.setText(label);
        b.setTextSize(15);
        b.setAllCaps(false);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(fg == 0 ? C_TXT : fg);
        b.setBackground(roundBg(bg == 0 ? C_BTN : bg, C_LINE, 14));
        b.setPadding(0, 0, 0, 0);
        b.setStateListAnimator(null);
        b.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { onAction(action); }
        });
        return b;
    }

    void setDot(View v, boolean ok) {
        GradientDrawable g = new GradientDrawable();
        g.setShape(GradientDrawable.OVAL);
        g.setColor(ok ? C_OK : C_BAD);
        v.setBackground(g);
    }

    void openUrl(String url) {
        try {
            android.content.Intent i = new android.content.Intent(
                    android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url));
            i.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(i);
        } catch (Exception e) {
            toast("打不开浏览器：" + e.getMessage());
        }
    }

    void toast(String s) {
        Toast.makeText(this, s, Toast.LENGTH_SHORT).show();
    }

    // ================================================== 标签切换
    void switchTab(int idx) {
        curTab = idx;
        LinearLayout[] pages = {pageHome, pageCtl, pageSelf, pageQr, pageLogs, pageQQ, pageMem};
        for (int i = 0; i < pages.length; i++) {
            pages[i].setVisibility(i == idx ? View.VISIBLE : View.GONE);
            paintTab(tabViews[i], i == idx);
        }
        if (idx == 1) { if (ctlGids.isEmpty()) ctlLoadGroups(); }
        if (idx == 2) runSelfCheck();
        if (idx == 3) { loadQr(); startQrTimer(); } else stopQrTimer();
        if (idx == 5) { loadQQProfile(); loadQZone(); }
        if (idx == 6) memLoad();
        if (idx == 4) {
            if (logMsg) { showConvList(); loadConversations(); }
            else { if (convCard != null) convCard.setVisibility(View.GONE);
                   if (chatCardView != null) chatCardView.setVisibility(View.GONE);
                   View rc = findRunCard(); if (rc != null) rc.setVisibility(View.VISIBLE);
                   loadLogs(); }
            startChatPoll();
        } else stopChatPoll();
    }

    // ================================================== 鱼的记忆（2026-11-07）
    LinearLayout buildMem() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);

        LinearLayout card = card();
        card.addView(text("鱼的记忆", 17, C_TXT, true));
        TextView tip = text("会淡忘的长期记忆：长期事实每轮都在；事件按「鲜活度 × 重要度 × 情绪」挑，命中的才注入。",
                12, C_SUB, false);
        tip.setPadding(0, dp(4), 0, dp(8));
        card.addView(tip);

        memWho = text("角色 feiyu · 会话：未选", 13, C_ACCENT, false);
        memWho.setPadding(0, dp(2), 0, dp(8));
        card.addView(memWho);

        LinearLayout r1 = new LinearLayout(this);
        r1.setOrientation(LinearLayout.HORIZONTAL);
        Button bP = memBtn("选角色");
        Button bC = memBtn("选会话");
        Button bR = memBtn("刷新");
        bP.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memPickPersona(); }
        });
        bC.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memPickChat(); }
        });
        bR.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memLoad(); }
        });
        r1.addView(bP); r1.addView(bC); r1.addView(bR);
        card.addView(r1);

        memStat = text("", 12, C_SUB, false);
        memStat.setPadding(0, dp(8), 0, dp(6));
        card.addView(memStat);

        memQ = new EditText(this);
        memQ.setHint("试一句，例：我生日是哪天");
        memQ.setTextSize(13);
        memQ.setTextColor(C_TXT);
        memQ.setHintTextColor(C_SUB);
        memQ.setBackground(roundBg(C_BTN, C_LINE, 10));
        memQ.setPadding(dp(10), dp(8), dp(10), dp(8));
        card.addView(memQ);

        Button bQ = memBtn("试召回：看会想起哪几条");
        bQ.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memRecall(); }
        });
        card.addView(bQ);

        memRecallOut = text("", 12, C_TXT, false);
        memRecallOut.setPadding(0, dp(6), 0, 0);
        card.addView(memRecallOut);
        page.addView(card);

        LinearLayout lc = card();
        lc.addView(text("记住了什么", 16, C_TXT, true));
        LinearLayout r2 = new LinearLayout(this);
        r2.setOrientation(LinearLayout.HORIZONTAL);
        Button bA = memBtn("加一条");
        Button bZ = memBtn("清空");
        bA.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memAddDialog(); }
        });
        bZ.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { memClearDialog(); }
        });
        r2.addView(bA); r2.addView(bZ);
        lc.addView(r2);

        memList = new LinearLayout(this);
        memList.setOrientation(LinearLayout.VERTICAL);
        lc.addView(memList);
        page.addView(lc);
        return page;
    }

    Button memBtn(String label) {
        Button b = btn(label, C_BTN, C_TXT, "");
        return b;
    }

    EditText memIn(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setTextSize(13);
        e.setTextColor(C_TXT);
        e.setHintTextColor(C_SUB);
        e.setBackground(roundBg(C_BTN, C_LINE, 10));
        e.setPadding(dp(10), dp(8), dp(10), dp(8));
        return e;
    }

    void memLoad() {
        getJson("/api/fishmem?persona=" + memPersona + "&chat=" + memChat, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null || o == null) {
                    if (memStat != null) memStat.setText("读取失败：" + (err == null ? "无返回" : err));
                    return;
                }
                memPaint(o);
            }
        });
    }

    void memPaint(JSONObject o) {
        if (memList == null) return;
        memChats.clear();
        JSONArray cs = o.optJSONArray("chats");
        if (cs != null) {
            for (int i = 0; i < cs.length(); i++) {
                JSONObject c = cs.optJSONObject(i);
                if (c != null) memChats.add(c);
            }
        }
        if (memChat.length() == 0 && memChats.size() > 0) {
            memChat = memChats.get(0).optString("chat");
            memLoad();
            return;
        }
        memWho.setText("角色 " + memPersona + " · 会话 " + (memChat.length() == 0 ? "未选" : memChat));
        JSONObject st = o.optJSONObject("stats");
        JSONObject cfg = o.optJSONObject("cfg");
        if (st != null) {
            int total = st.optInt("total"), live = st.optInt("live");
            memStat.setText(String.format(
                    "共 %d 条（长期 %d / 事件 %d）· 已取代 %d · 今天 %d/%d · 累计被想起 %d 次",
                    total, st.optInt("facts"), st.optInt("events"), total - live,
                    st.optInt("today"), (cfg == null || cfg.optInt("daily") <= 0) ? 8 : cfg.optInt("daily"), st.optInt("hits")));
        }
        memList.removeAllViews();
        JSONArray items = o.optJSONArray("items");
        if (items == null || items.length() == 0) {
            memList.addView(text(memChat.length() == 0 ? "还没有记忆（先聊几句，或者点「加一条」）"
                    : "这个会话还没记下什么", 12, C_SUB, false));
            return;
        }
        for (int i = 0; i < items.length(); i++) {
            final JSONObject it = items.optJSONObject(i);
            if (it == null) continue;
            boolean dead = it.optBoolean("superseded");
            String key = it.optString("key");
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setBackground(roundBg(C_CARD, C_LINE, 10));
            row.setPadding(dp(10), dp(8), dp(10), dp(8));
            LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            rlp.setMargins(0, 0, 0, dp(8));
            row.setLayoutParams(rlp);
            row.addView(text(it.optString("date") + (dead ? " · 已被取代" : "")
                    + (key.length() == 0 ? "" : "   [" + key + "]"), 11,
                    dead ? C_SUB : C_ACCENT, false));
            row.addView(text(it.optString("text"), 14, dead ? C_SUB : C_TXT, false));
            row.addView(text(String.format("鲜活 %.0f%% · 重要 %.0f%% · 想起 %d 次%s",
                    it.optDouble("vitality") * 100, it.optDouble("importance") * 100,
                    it.optInt("hits"), "fact".equals(it.optString("kind")) ? " · 长期" : ""),
                    11, C_SUB, false));
            final String sid = it.optString("id");
            final String stext = it.optString("text");
            row.setOnLongClickListener(new View.OnLongClickListener() {
                public boolean onLongClick(View v) { memDelDialog(sid, stext); return true; }
            });
            memList.addView(row);
        }
    }

    void memPickPersona() {
        final String[] ps = {"feiyu", "dsh", "taffy", "feiyu_adult"};
        new AlertDialog.Builder(this)
                .setTitle("选角色")
                .setItems(ps, new android.content.DialogInterface.OnClickListener() {
                    public void onClick(android.content.DialogInterface d, int w) {
                        memPersona = ps[w];
                        memChat = "";
                        memLoad();
                    }
                }).show();
    }

    void memPickChat() {
        if (memChats.size() == 0) { toast("这个角色还没有会话记忆"); return; }
        final String[] labels = new String[memChats.size()];
        for (int i = 0; i < memChats.size(); i++) {
            JSONObject c = memChats.get(i);
            labels[i] = c.optString("chat") + "   （" + c.optInt("live") + " 条，长期 "
                    + c.optInt("facts") + "）";
        }
        new AlertDialog.Builder(this)
                .setTitle("选会话")
                .setItems(labels, new android.content.DialogInterface.OnClickListener() {
                    public void onClick(android.content.DialogInterface d, int w) {
                        memChat = memChats.get(w).optString("chat");
                        memLoad();
                    }
                }).show();
    }

    void memPost(JSONObject body, final JsonCb cb) {
        try {
            body.put("persona", memPersona);
            body.put("chat", memChat);
        } catch (Exception ignored) {
        }
        postJsonBody("/api/fishmem", body, cb);
    }

    void memRecall() {
        if (memChat.length() == 0) { toast("先选会话"); return; }
        String q = memQ.getText().toString().trim();
        if (q.length() == 0) { toast("先写一句要试的话"); return; }
        JSONObject body = new JSONObject();
        try {
            body.put("act", "recall");
            body.put("q", q);
        } catch (Exception ignored) {
        }
        memPost(body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null || o == null) { memRecallOut.setText("失败：" + err); return; }
                if (!o.optBoolean("ok")) { memRecallOut.setText("失败：" + o.optString("error")); return; }
                StringBuilder sb = new StringBuilder();
                String blk = o.optString("block");
                sb.append(blk.length() == 0 ? "（这条什么都不会注入）" : blk);
                JSONArray sk = o.optJSONArray("skipped");
                if (sk != null && sk.length() > 0) {
                    sb.append("\n\n没进来的：");
                    for (int i = 0; i < sk.length() && i < 6; i++) {
                        JSONObject s = sk.optJSONObject(i);
                        if (s == null) continue;
                        sb.append("\n· ").append(s.optString("text")).append("：").append(s.optString("why"));
                    }
                }
                memRecallOut.setText(sb.toString());
            }
        });
    }

    void memAddDialog() {
        if (memChat.length() == 0) { toast("先选会话"); return; }
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(10), dp(16), dp(6));
        final EditText tIn = memIn("记什么？例：她生日是 3 月 8 日");
        final EditText kIn = memIn("键（可留空）例：她.生日 —— 同键只留最新");
        box.addView(tIn);
        box.addView(kIn);
        new AlertDialog.Builder(this)
                .setTitle("加一条记忆")
                .setView(box)
                .setPositiveButton("记下", new android.content.DialogInterface.OnClickListener() {
                    public void onClick(android.content.DialogInterface d, int w) {
                        JSONObject body = new JSONObject();
                        try {
                            body.put("act", "add");
                            body.put("text", tIn.getText().toString().trim());
                            body.put("key", kIn.getText().toString().trim());
                        } catch (Exception ignored) {
                        }
                        memPost(body, new JsonCb() {
                            public void on(JSONObject o, String err) {
                                toast(err != null ? "失败：" + err
                                        : (o != null && o.optBoolean("ok") ? "记下了" : "没记成"));
                                memLoad();
                            }
                        });
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void memDelDialog(final String id, String text) {
        new AlertDialog.Builder(this)
                .setTitle("删掉这条？")
                .setMessage(text)
                .setPositiveButton("删", new android.content.DialogInterface.OnClickListener() {
                    public void onClick(android.content.DialogInterface d, int w) {
                        JSONObject body = new JSONObject();
                        try {
                            body.put("act", "del");
                            body.put("id", id);
                        } catch (Exception ignored) {
                        }
                        memPost(body, new JsonCb() {
                            public void on(JSONObject o, String err) {
                                toast(err != null ? "失败：" + err : "删掉了");
                                memLoad();
                            }
                        });
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void memClearDialog() {
        new AlertDialog.Builder(this)
                .setTitle("清空记忆")
                .setMessage("把 " + memChat + " 的记忆全部删掉？")
                .setPositiveButton("清空", new android.content.DialogInterface.OnClickListener() {
                    public void onClick(android.content.DialogInterface d, int w) {
                        JSONObject body = new JSONObject();
                        try {
                            body.put("act", "clear");
                        } catch (Exception ignored) {
                        }
                        memPost(body, new JsonCb() {
                            public void on(JSONObject o, String err) {
                                toast(err != null ? "失败：" + err : "清空了");
                                memLoad();
                            }
                        });
                    }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void paintTab(TextView t, boolean on) {
        t.setBackground(roundBg(on ? C_ACCENT_D : C_CARD, on ? 0 : C_LINE, 12));
        t.setTextColor(on ? 0xFFDFE7FF : C_SUB);
    }

    void startQrTimer() {
        stopQrTimer();
        qrTimer = new Runnable() {
            public void run() {
                // ⚠️ 2026-10-28 修：这里原来判的是 `curTab == 2`（那是**自检**页），
                // 扫码页是 3 —— 所以「每 20 秒自动换新」一直没生效（注释写了、代码没跑）。
                if (curTab == 3) { loadQr(); ui.postDelayed(this, 20000); }
            }
        };
        ui.postDelayed(qrTimer, 20000);
    }

    void stopQrTimer() {
        if (qrTimer != null) { ui.removeCallbacks(qrTimer); qrTimer = null; }
    }

    // ================================================== 网络
    interface JsonCb { void on(JSONObject o, String err); }
    interface BinCb { void on(Bitmap bmp, String err); }

    String api(String path) {
        String sep = path.contains("?") ? "&" : "?";
        return BASE + path + sep + "token=" + TOKEN;
    }

    /** 下载一张图（梗库表情包预览用）；失败返回 null */
    byte[] httpBytes(String path) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(api(path)).openConnection();
            c.setConnectTimeout(12000);
            c.setReadTimeout(20000);
            if (c.getResponseCode() >= 400) return null;
            InputStream is = c.getInputStream();
            byte[] raw = readAllBytes(is);
            is.close();
            return raw;
        } catch (Throwable t) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    java.io.File plThumbFile(String id) {
        try {
            java.io.File d = new java.io.File(getCacheDir(), "photolib");
            if (!d.exists()) d.mkdirs();
            String safe = id == null ? "" : id.replaceAll("[^A-Za-z0-9_.-]", "_");
            return new java.io.File(d, safe + ".jpg");
        } catch (Throwable t) {
            return null;
        }
    }

    Bitmap httpBitmap(String path) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(api(path)).openConnection();
            c.setConnectTimeout(12000);
            c.setReadTimeout(20000);
            if (c.getResponseCode() >= 400) return null;
            InputStream is = c.getInputStream();
            Bitmap bm = BitmapFactory.decodeStream(is);
            is.close();
            return bm;
        } catch (Throwable t) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    /** 下载一个**完整 URL**的图（聊天里的图 / QQ 表情包走这条路，不加 token）。 */
    Bitmap httpBitmapUrl(String url) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(12000);
            c.setReadTimeout(25000);
            c.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android) FishConsole");
            if (c.getResponseCode() >= 400) return null;
            InputStream is = c.getInputStream();
            Bitmap bm = BitmapFactory.decodeStream(is);
            is.close();
            return bm;
        } catch (Throwable t) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    // ---- 聊天里的图（2026-10-28：用户「看不到别人发的图和表情包」）----
    final java.util.HashMap<String, Bitmap> chatPicCache = new java.util.HashMap<String, Bitmap>();

    boolean picPlaceholder(String s) {
        return s == null || s.length() == 0 || s.equals("[图片]") || s.equals("[表情]");
    }

    /** 气泡里的图：别人发的（img=外链）/ 她自己发的（loc=服务器 chatimg）/ 表情包（stk=stickers 文件名）。 */
    void loadBubblePic(final String img, final String loc, final String stk,
                       final ImageView iv, final TextView fb) {
        String url;
        if (img.length() > 0) url = img;
        else if (loc.length() > 0) url = api("/api/imgfile?f=" + java.net.URLEncoder.encode(loc));
        else url = api("/api/stickers/thumb?id=" + java.net.URLEncoder.encode(stk));
        Bitmap hit = chatPicCache.get(url);
        if (hit != null) {
            iv.setImageBitmap(hit);
            if (fb != null && picPlaceholder(fb.getText().toString())) fb.setVisibility(View.GONE);
            return;
        }
        pool.execute(new Runnable() {
            public void run() {
                final Bitmap bmp = httpBitmapUrl(url);
                ui.post(new Runnable() {
                    public void run() {
                        if (bmp != null) {
                            if (chatPicCache.size() > 200) chatPicCache.clear();
                            chatPicCache.put(url, bmp);
                            iv.setImageBitmap(bmp);
                            if (fb != null && picPlaceholder(fb.getText().toString())) fb.setVisibility(View.GONE);
                        } else if (fb != null) {
                            // 图过期 / 下载失败：退回文字，别留一个空框
                            fb.setText((stk.length() > 0 ? "[表情]" : "[图片]") + "（加载失败，链接可能已过期）");
                            fb.setVisibility(View.VISIBLE);
                        }
                    }
                });
            }
        });
    }

    // ---- 双击看大图（2026-10-28 用户：「图片可以双击放大，但不能误触」）----
    // 防误触：① 只有图片区域认双击（onTouch 返回 false，不吞事件 → 气泡长按菜单照旧）
    //        ② 大图刚开的前 400ms 不理关闭动作（双击第二下不会立刻关掉）
    //        ③ 关闭只认「✕ / 点黑边 / 返回键」，点图片本身不关
    Dialog bigDlg;
    long bigOpenedAt = 0;

    void showBigPic(Bitmap bm) {
        if (bm == null || bigDlg != null) return;
        final Dialog d = new Dialog(this, android.R.style.Theme_Black_NoTitleBar_Fullscreen);
        final FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(0xFF000000);

        final ImageView iv = new ImageView(this);
        // 尺寸自己算：按屏幕留出黑边（黑边可点关闭），图本身按比例铺满 94%×82%
        int scrW = getResources().getDisplayMetrics().widthPixels;
        int scrH = getResources().getDisplayMetrics().heightPixels;
        int maxW = (int) (scrW * 0.94f), maxH = (int) (scrH * 0.82f);
        int bw = Math.max(1, bm.getWidth()), bh = Math.max(1, bm.getHeight());
        float fit = Math.min(maxW / (float) bw, maxH / (float) bh);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                Math.max(1, (int) (bw * fit)), Math.max(1, (int) (bh * fit)));
        lp.gravity = Gravity.CENTER;
        iv.setLayoutParams(lp);
        iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
        iv.setImageBitmap(bm);
        root.addView(iv);

        final TextView x = text("✕", 20, 0xFFFFFFFF, false);
        FrameLayout.LayoutParams xp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        xp.gravity = Gravity.TOP | Gravity.END;
        xp.setMargins(0, dp(28), dp(22), 0);
        x.setLayoutParams(xp);
        x.setPadding(dp(14), dp(6), dp(14), dp(6));
        root.addView(x);

        TextView tip = text("双击切换 1x / 2.5x · 点黑边或按返回关闭", 11, 0xB0FFFFFF, false);
        FrameLayout.LayoutParams tp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        tp.setMargins(0, 0, 0, dp(30));
        tip.setLayoutParams(tp);
        root.addView(tip);

        d.setContentView(root);
        bigOpenedAt = System.currentTimeMillis();

        View.OnClickListener closer = new View.OnClickListener() {
            public void onClick(View v) {
                if (System.currentTimeMillis() - bigOpenedAt < 400) return;   // 防误触②
                d.dismiss();
            }
        };
        x.setOnClickListener(closer);
        root.setOnClickListener(closer);       // 点黑边关
        iv.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { }    // 点图片本身什么都不做
        });
        // 大图里双击 = 1x / 2.5x 切换（同样自己数时间，系统 GestureDetector 在嵌套滚动里不稳）
        iv.setOnTouchListener(new View.OnTouchListener() {
            long last = 0;
            float lx = 0, ly = 0;

            public boolean onTouch(View v, MotionEvent ev) {
                if (ev.getActionMasked() == MotionEvent.ACTION_UP) {
                    long now = System.currentTimeMillis();
                    if (last > 0 && now - last <= 400
                            && Math.abs(ev.getX() - lx) <= dp(36)
                            && Math.abs(ev.getY() - ly) <= dp(36)) {
                        last = 0;
                        boolean big = iv.getScaleX() > 1.5f;
                        iv.setScaleX(big ? 1f : 2.5f);
                        iv.setScaleY(big ? 1f : 2.5f);
                    } else {
                        last = now;
                        lx = ev.getX();
                        ly = ev.getY();
                    }
                }
                return false;
            }
        });
        d.setOnDismissListener(new DialogInterface.OnDismissListener() {
            public void onDismiss(DialogInterface di) {
                bigDlg = null;
                iv.setImageDrawable(null);
            }
        });
        bigDlg = d;
        d.show();
    }

    void getJson(final String path, final JsonCb cb) {
        pool.execute(new Runnable() {
            public void run() {
                String err = null;
                JSONObject obj = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api(path)).openConnection();
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(60000);
                    int code = c.getResponseCode();
                    InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
                    String body = read(is);
                    if (code == 401) err = "token 无效";
                    else if (code >= 400) err = "服务器返回 " + code;
                    else obj = new JSONObject(body);
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final JSONObject fo = obj;
                final String fe = err;
                ui.post(new Runnable() { public void run() { cb.on(fo, fe); } });
            }
        });
    }

    void postAction(final String name, final JsonCb cb) {
        pool.execute(new Runnable() {
            public void run() {
                String err = null;
                JSONObject obj = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api("/api/action")).openConnection();
                    c.setDoOutput(true);
                    c.setRequestMethod("POST");
                    c.setRequestProperty("Content-Type", "application/json");
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(150000);
                    String body = "{\"name\":\"" + name + "\"}";
                    c.getOutputStream().write(body.getBytes("UTF-8"));
                    int code = c.getResponseCode();
                    InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
                    String resp = read(is);
                    if (code == 401) err = "token 无效";
                    else obj = new JSONObject(resp);
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final JSONObject fo = obj;
                final String fe = err;
                ui.post(new Runnable() { public void run() { cb.on(fo, fe); } });
            }
        });
    }

    void getImage(final String path, final BinCb cb) {
        pool.execute(new Runnable() {
            public void run() {
                Bitmap bmp = null;
                String err = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api(path)).openConnection();
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(90000);
                    int code = c.getResponseCode();
                    if (code != 200) {
                        err = code == 404 ? "暂时没有二维码" : ("服务器返回 " + code);
                    } else {
                        byte[] data = readBytes(c.getInputStream());
                        bmp = BitmapFactory.decodeByteArray(data, 0, data.length);
                        if (bmp == null) err = "二维码解析失败";
                    }
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final Bitmap fb = bmp;
                final String fe = err;
                ui.post(new Runnable() { public void run() { cb.on(fb, fe); } });
            }
        });
    }

    String read(InputStream is) throws Exception {
        if (is == null) return "";
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) > 0) bos.write(buf, 0, n);
        is.close();
        return new String(bos.toByteArray(), "UTF-8");
    }

    byte[] readBytes(InputStream is) throws Exception {
        if (is == null) return new byte[0];
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) > 0) bos.write(buf, 0, n);
        is.close();
        return bos.toByteArray();
    }

    // ================================================== 数据加载
    // 快速探活：能连通就说明服务器没问题，只是某个接口慢
    void probe() {
        pool.execute(new Runnable() {
            public void run() {
                String msg;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api("/api/health")).openConnection();
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(20000);
                    int code = c.getResponseCode();
                    msg = code == 200 ? "网络正常，服务器可达" : ("服务器返回 " + code + "（token 可能不对）");
                } catch (Exception e) {
                    msg = "连不上 " + BASE + "\n可能是：\n· 云服务器防火墙没放行 8090\n· 手机网络（4G/WiFi）对应的 IP 不在白名单\n· fishweb 服务没跑";
                } finally {
                    if (c != null) c.disconnect();
                }
                final String m = msg;
                ui.post(new Runnable() { public void run() {
                    new AlertDialog.Builder(MainActivity.this)
                            .setTitle("连接诊断")
                            .setMessage(m)
                            .setPositiveButton("知道了", null)
                            .show();
                }});
            }
        });
    }

    void loadStatus() {
        getJson("/api/diag", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    vVerdict.setText("连不上服务器");
                    vHint.setText(err + " · 点这里诊断");
                    setDot(dot, false);
                    vFlow.setText("上次：" + prefs.getString("last_flow", "–"));
                    vStamp.setText(prefs.getString("last_ts", "--:--:--"));
                    return;
                }
                try {
                    JSONObject s = o.getJSONObject("status");
                    String verdict = o.optString("verdict", "?");
                    vVerdict.setText(verdict);
                    vHint.setText(o.optString("hint", ""));
                    setDot(dot, verdict.contains("在线"));
                    vSvc.setText(s.optString("svc"));
                    vNap.setText(s.optBoolean("napcat") ? "在跑" : "没起来");
                    vMem.setText(s.optString("mem", "–"));
                    vUp.setText(s.optString("up", "–").replace(" CST", ""));
                    String flow = "收 " + s.optInt("rx") + " / 发 " + s.optInt("tx");
                    vFlow.setText(flow);
                    String ts = s.optString("ts", "");
                    vStamp.setText(ts);
                    prefs.edit().putString("last_flow", flow).putString("last_ts", ts).apply();
                } catch (Exception e) {
                    vVerdict.setText("数据解析失败");
                }
            }
        });
        getJson("/api/health", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null || o == null) return;
                int count = o.optInt("count", 0);
                String last = o.optString("last", "");
                if ("active".equals(o.optString("timer", ""))) {
                    vDog.setText(count > 0
                            ? ("运行中 · 已自愈 " + count + " 次\n最近：" + last)
                            : "运行中 · 还没自愈过（一直没掉线）");
                } else {
                    vDog.setText("没在跑（fishdog.timer 不活跃）");
                }
            }
        });
    }

    void runSelfCheck() {
        sVerdict.setText("检查中…");
        sHint.setText("");
        getJson("/api/selfcheck", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    sVerdict.setText("连不上服务器");
                    sHint.setText(err);
                    setDot(sDot, false);
                    sItems.setText("");
                    return;
                }
                try {
                    String verdict = o.optString("verdict", "?");
                    sVerdict.setText(verdict);
                    sHint.setText(o.optString("hint", ""));
                    setDot(sDot, verdict.contains("在线"));
                    JSONArray items = o.optJSONArray("items");
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; items != null && i < items.length(); i++) {
                        JSONObject it = items.getJSONObject(i);
                        sb.append(it.optBoolean("ok") ? "✓  " : "!  ")
                          .append(it.optString("name"))
                          .append("   ")
                          .append(it.optString("detail"))
                          .append("\n");
                    }
                    sItems.setText(sb.toString().trim());
                } catch (Exception e) {
                    sVerdict.setText("数据解析失败");
                }
            }
        });
    }

    void loadQr() {
        qrTip.setText("正在取二维码…");
        getImage("/api/qr?fresh=1", new BinCb() {
            public void on(Bitmap bmp, String err) {
                if (bmp != null) {
                    qrImage.setImageBitmap(bmp);
                    qrTip.setText("每 20 秒自动换新；用手机 QQ → 扫一扫。");
                } else {
                    qrTip.setText(err == null ? "取不到二维码" : err);
                }
            }
        });
    }

    void showConvList() {
        chatTarget = null;
        if (convCard != null) convCard.setVisibility(View.VISIBLE);
        if (chatCardView != null) chatCardView.setVisibility(View.GONE);
        View runCard = findRunCard();
        if (runCard != null) runCard.setVisibility(View.GONE);
    }

    void startChatPoll() {
        stopChatPoll();
        chatPoll = new Runnable() {
            public void run() {
                if (chatTarget != null) {
                    loadMessages();                                   // 聊天里：5 秒同步一次
                    ui.postDelayed(this, 5000);
                } else {
                    if (logMsg && curTab == 4) loadConversations();   // 会话列表：12 秒刷一次
                    ui.postDelayed(this, 12000);
                }
            }
        };
        ui.postDelayed(chatPoll, chatTarget != null ? 5000 : 12000);
    }

    void stopChatPoll() {
        if (chatPoll != null) ui.removeCallbacks(chatPoll);
        chatPoll = null;
    }

    void openChat(String chat, String title) {
        chatTarget = chat;
        chatTitle = (title == null || title.length() == 0) ? chat : title;
        chatSigs.clear();                       // 换会话 → 必须整块重建
        quoteMsg = null;                        // 别把上一个群的引用带过来
        atList.clear();
        paintCompose();
        scrollOnNextLoad = true;                // 打开会话就贴到最新一条
        stickBottom = true;
        if (convCard != null) convCard.setVisibility(View.GONE);
        if (chatCardView != null) chatCardView.setVisibility(View.VISIBLE);
        setChatTitle(chatTitle);
        loadMessages();
        startChatPoll();
        fitChatHeight();                        // 让输入框不用划就能看见
    }

    void setChatTitle(String s) {
        if (chatCardView == null) return;
        try {
            TextView t = (TextView) chatCardView.findViewWithTag("chattitle");
            if (t != null) t.setText(s);
        } catch (Exception e) { }
    }

    void closeChat() {
        chatTarget = null;
        quoteMsg = null;
        atList.clear();
        paintCompose();
        showConvList();
        loadConversations();
        startChatPoll();
    }

    String cacheGet(String key) {
        try { return prefs.getString("c_" + key, ""); } catch (Exception e) { return ""; }
    }

    void cachePut(String key, String val) {
        try { prefs.edit().putString("c_" + key, val).apply(); } catch (Exception e) { }
    }

    // 会话头像配色（跟电脑端一致：同一个 key 颜色固定）
    static final int[] AVA = {0xFF3F6AEF, 0xFF4CAF50, 0xFFF57C00, 0xFF9C27B0, 0xFFE91E63, 0xFF00ACC1};

    int avatarColor(String key) {
        int h = 0;
        for (int i = 0; i < key.length(); i++) h = (h * 31 + key.charAt(i)) & 0x7fffffff;
        return AVA[h % AVA.length];
    }

    String hhmm(String ts) {
        if (ts == null) return "";
        return ts.length() >= 5 ? ts.substring(0, 5) : ts;
    }

    /** QQ 风格的会话列表：圆圈头像 + 名字 + 最后一句 + 时间 / 条数 */
    void renderConversations(JSONArray arr) {
        convBox.removeAllViews();
        if (arr == null || arr.length() == 0) {
            convBox.addView(text("最近没有消息", 13, C_SUB, false));
            return;
        }
        for (int i = 0; i < arr.length(); i++) {
            try {
                JSONObject it = arr.getJSONObject(i);
                final String key = it.optString("chat");
                String title = it.optString("title", key);
                if (title == null || title.length() == 0) title = key;
                final String showTitle = title;
                String last = it.optString("last");
                if (last.startsWith("【")) last = last.contains("表情") ? "[表情]" : "[图片]";
                if ("out".equals(it.optString("dir"))) last = "我：" + last;
                int count = it.optInt("count");

                LinearLayout row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setPadding(dp(4), dp(10), dp(4), dp(10));
                row.setBackground(roundBg(0, 0, 10));
                row.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) { openChat(key, showTitle); }
                });

                // 圆圈头像：取名字第一个字
                TextView av = text(title.substring(0, 1), 15, 0xFFFFFFFF, true);
                av.setGravity(Gravity.CENTER);
                GradientDrawable ag = new GradientDrawable();
                ag.setShape(GradientDrawable.OVAL);
                ag.setColor(avatarColor(key));
                av.setBackground(ag);
                LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(dp(42), dp(42));
                alp.setMargins(0, 0, dp(11), 0);
                av.setLayoutParams(alp);
                row.addView(av);

                // 中间：名字 + 最后一句
                LinearLayout mid = new LinearLayout(this);
                mid.setOrientation(LinearLayout.VERTICAL);
                mid.setLayoutParams(new LinearLayout.LayoutParams(
                        0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
                TextView t1 = text(title, 14, C_TXT, true);
                t1.setMaxLines(1);
                t1.setEllipsize(TextUtils.TruncateAt.END);
                mid.addView(t1);
                TextView t2 = text(last, 12, C_SUB, false);
                t2.setMaxLines(1);
                t2.setEllipsize(TextUtils.TruncateAt.END);
                t2.setPadding(0, dp(3), 0, 0);
                mid.addView(t2);
                row.addView(mid);

                // 右侧：时间 + 条数
                LinearLayout right = new LinearLayout(this);
                right.setOrientation(LinearLayout.VERTICAL);
                right.setGravity(Gravity.END);
                LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                rlp.setMargins(dp(8), 0, 0, 0);
                right.setLayoutParams(rlp);
                TextView rt = text(hhmm(it.optString("ts")), 10, C_SUB, false);
                rt.setGravity(Gravity.END);
                right.addView(rt);
                TextView rb = text(count + " 条", 10, C_SUB, false);
                rb.setGravity(Gravity.END);
                rb.setPadding(0, dp(4), 0, 0);
                right.addView(rb);
                row.addView(right);

                convBox.addView(row);

                View div = new View(this);
                div.setBackgroundColor(C_LINE);
                LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT, dp(1));
                dlp.setMargins(dp(57), 0, 0, 0);
                div.setLayoutParams(dlp);
                if (i < arr.length() - 1) convBox.addView(div);
            } catch (Exception e) { }
        }
    }

    void loadConversations() {
        String cached = cacheGet("conv");
        boolean painted = false;
        if (cached.length() > 10) {
            try {
                renderConversations(new JSONObject(cached).optJSONArray("items"));
                painted = true;
            } catch (Exception e) { }
        }
        if (!painted) {
            convBox.removeAllViews();
            convBox.addView(text("加载中…", 13, C_SUB, false));
        }
        if (convBox.getChildCount() == 1 && painted) {
            TextView tip = text("后台刷新中…", 10, C_SUB, false);
            tip.setTag("refreshing");
            convBox.addView(tip);
        }
        getJson("/api/conversations", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (convBox.getChildCount() == 0) convBox.addView(text(err, 13, C_BAD, false));
                    return;
                }
                renderConversations(o.optJSONArray("items"));
                cachePut("conv", o.toString());
            }
        });
    }

    void showChat(boolean chat) {
        if (chatBox != null) chatBox.setVisibility(chat ? View.VISIBLE : View.GONE);
        View p = chatBox == null ? null : (View) chatBox.getParent();
        if (p != null) p.setVisibility(chat ? View.VISIBLE : View.GONE);
        View runCard = root == null ? null : findRunCard();
        if (runCard != null) runCard.setVisibility(chat ? View.GONE : View.VISIBLE);
    }

    View findRunCard() {
        if (runBox == null) return null;
        View v = runBox;
        while (v != null && !(v instanceof LinearLayout && v.getTag() != null)) {
            if (!(v.getParent() instanceof View)) break;
            v = (View) v.getParent();
        }
        return v;
    }

    // 已渲染消息的签名；用来判断"这次刷新到底有没有新内容"
    final List<String> chatSigs = new ArrayList<String>();

    String msgSig(JSONObject m) {
        return m.optString("dir") + "|" + m.optString("dt") + "|" + m.optString("ts")
                + "|" + m.optString("who") + "|" + m.optString("text");
    }

    /**
     * 渲染消息。返回 true 表示界面真的变了。
     *
     * 以前是每 5 秒 removeAllViews + 重建 150 个气泡，打字时正好撞上就卡输入
     * （手机端尤其明显）。现在：内容没变直接跳过；只多了新消息就追加。
     */
    boolean renderMessages(JSONArray arr) {
        List<String> sigs = new ArrayList<String>();
        int n = arr == null ? 0 : arr.length();
        for (int i = 0; i < n; i++) {
            try { sigs.add(msgSig(arr.getJSONObject(i))); } catch (Exception e) { }
        }
        if (arr == null || arr.length() == 0) {
            if (chatSigs.isEmpty() && chatBox.getChildCount() > 0) return false;
            chatBox.removeAllViews();
            chatSigs.clear();
            chatBox.addView(text("这个会话最近没有消息", 13, C_SUB, false));
            return true;
        }
        if (sigs.equals(chatSigs)) return false;          // 一模一样，不动界面

        // 旧列表是新列表的前缀 → 只追加增量
        int old = chatSigs.size();
        if (old > 0 && sigs.size() > old && sigs.subList(0, old).equals(chatSigs)) {
            for (int i = old; i < arr.length(); i++) {
                try { addBubble(arr.getJSONObject(i)); } catch (Exception e) { }
            }
            chatSigs.clear();
            chatSigs.addAll(sigs);
            return true;
        }

        // 否则整体重建（切会话、消息被删、或历史被服务端重排）
        chatBox.removeAllViews();
        chatSigs.clear();
        chatSigs.addAll(sigs);
        for (int i = 0; i < arr.length(); i++) {
            try { addBubble(arr.getJSONObject(i)); } catch (Exception e) { }
        }
        return true;
    }

    void loadMessages() {
        String key = "msg_" + (chatTarget == null ? "" : chatTarget);
        String cached = cacheGet(key);
        boolean painted = false;
        if (cached.length() > 10) {
            try {
                renderMessages(new JSONObject(cached).optJSONArray("messages"));
                painted = true;
                if (scrollOnNextLoad) scrollToBottom();   // 先用缓存画出来的时候也贴底
            } catch (Exception e) { }
        }
        if (!painted) {
            chatBox.removeAllViews();
            chatSigs.clear();
            chatBox.addView(text("加载中…", 13, C_SUB, false));
        }
        String path = "/api/messages?limit=150";
        if (chatTarget != null && chatTarget.length() > 0) {
            try { path += "&chat=" + URLEncoder.encode(chatTarget, "UTF-8"); } catch (Exception e) { }
        }
        getJson(path, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) {
                    if (chatBox.getChildCount() == 0) chatBox.addView(text(err, 13, C_BAD, false));
                    return;
                }
                boolean wasBottom = isAtBottom();
                if (wasBottom) stickBottom = true;      // 翻回底部了，继续跟着最新一条
                boolean changed = renderMessages(o.optJSONArray("messages"));
                cachePut("msg_" + (chatTarget == null ? "" : chatTarget), o.toString());
                String t = o.optString("title");
                if (t != null && t.length() > 0) { chatTitle = t; setChatTitle(t); }
                // 正在输入时别抢滚动（IME 弹着的时候一滚就"卡输入"）
                boolean typing = chatInput != null && chatInput.hasFocus();
                if (scrollOnNextLoad) {
                    scrollOnNextLoad = false;          // 打开会话：无条件贴到最新一条
                    scrollToBottom();
                } else if (changed && wasBottom && !typing) scrollToBottom();
            }
        });
    }

    void addChatDivider(String chat) {
        if (chat == null || chat.length() == 0) return;
        TextView t = text(chat, 11, C_SUB, false);
        t.setGravity(Gravity.CENTER);
        t.setPadding(0, dp(10), 0, dp(6));
        chatBox.addView(t);
    }

    /**
     * 气泡容器：把「最大宽度」限制在容器这一层。
     *
     * 以前是给里面的 TextView 设 setMaxWidth，结果 TextView 按 maxWidth 量、却按更窄的
     * 宽度布局，长消息只画出前几行（用户看到的就是"只显示一半的字"）。
     * 这里在 onMeasure 里把测量规格压到 maxW，孩子量多少就排多少，不会再对不上。
     */
    class BubbleBox extends LinearLayout {
        int maxW = Integer.MAX_VALUE;

        BubbleBox() {
            super(MainActivity.this);
            setOrientation(VERTICAL);
            maxW = (int) (getResources().getDisplayMetrics().widthPixels * 0.72f);
        }

        @Override
        protected void onMeasure(int wSpec, int hSpec) {
            int size = MeasureSpec.getSize(wSpec);
            int mode = MeasureSpec.getMode(wSpec);
            if (size > maxW) {
                wSpec = MeasureSpec.makeMeasureSpec(maxW,
                        mode == MeasureSpec.UNSPECIFIED ? MeasureSpec.AT_MOST : mode);
            }
            super.onMeasure(wSpec, hSpec);
        }
    }

    void addBubble(final JSONObject m) {
        boolean out = "out".equals(m.optString("dir"));
        String who = m.optString("who");
        String txt = m.optString("text");
        String ts = m.optString("ts");
        if ("media".equals(m.optString("kind"))) {
            txt = txt.contains("表情") ? "[表情]" : "[图片]";
        }

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.VERTICAL);
        row.setPadding(0, dp(4), 0, dp(4));
        row.setGravity(out ? Gravity.END : Gravity.START);

        TextView head = text((out ? "" : (who + "  ")) + ts, 10, C_SUB, false);
        head.setPadding(dp(6), 0, dp(6), dp(2));
        row.addView(head);

        BubbleBox bubble = new BubbleBox();
        bubble.setPadding(dp(12), dp(9), dp(12), dp(9));
        bubble.setBackground(roundBg(out ? 0xFF3F6AEF : 0xFF22252A, out ? 0 : C_LINE, 14));
        bubble.setLongClickable(true);

        JSONObject q = m.optJSONObject("quote");
        if (q != null) {
            String qw = q.optString("who");
            String qt = q.optString("text");
            LinearLayout qbox = new LinearLayout(this);
            qbox.setOrientation(LinearLayout.VERTICAL);
            qbox.setPadding(dp(8), dp(5), dp(8), dp(5));
            qbox.setBackground(roundBg(out ? 0x40FFFFFF : C_CARD, 0, 6));
            LinearLayout.LayoutParams qlp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            qlp.setMargins(0, 0, 0, dp(5));
            qbox.setLayoutParams(qlp);
            if (qw.length() > 0) {
                TextView qn = text(qw, 10, out ? 0xFFFFFFFF : C_ACCENT, true);
                qbox.addView(qn);
            }
            TextView qv = text(qt.length() > 0 ? qt : "引用了一条消息", 11,
                    out ? 0xE6FFFFFF : C_TXT, false);
            qv.setMaxLines(4);                       // 以前 2 行，长引用只剩半句
            qv.setEllipsize(TextUtils.TruncateAt.END);
            qv.setMaxWidth((int) (getResources().getDisplayMetrics().widthPixels * 0.66f));
            qbox.addView(qv);
            bubble.addView(qbox);
        }

        TextView bubbleText = text(txt, 14, out ? 0xFFFFFFFF : C_TXT, false);
        // 有图就先画图（别人发的图 / 表情包），文字只当兜底：加载成功就把「[图片]」藏起来
        String img = m.optString("img");
        String loc = m.optString("loc");
        String stk = m.optString("stk");
        if (img.length() > 0 || loc.length() > 0 || stk.length() > 0) {
            final ImageView pic = new ImageView(this);
            pic.setAdjustViewBounds(true);
            // 表情包比照片小，但别小到看不清（以前 120dp 太抠）
            int cap = dp(img.length() > 0 ? 230 : 170);
            pic.setMaxWidth(cap);
            pic.setMaxHeight(cap);
            pic.setScaleType(ImageView.ScaleType.FIT_CENTER);
            LinearLayout.LayoutParams plp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            plp.setMargins(0, 0, 0, dp(3));
            pic.setLayoutParams(plp);
            bubble.addView(pic);
            loadBubblePic(img, loc, stk, pic, bubbleText);
            // 双击放大：检测器挂在**气泡**上（挂图片控件上收不到第二下）。
            // 不用系统 GestureDetector —— 实测它在这套嵌套滚动里判不出双击，自己数时间更稳：
            //   两次「抬起」间隔 ≤ 400ms、且手指没移动超过 36dp 才算双击（不乱触发）。
            // 返回 false 不吞手势：长按菜单、上下滑动都照旧。
            final long[] tapAt = new long[] { 0 };
            final float[] tapX = new float[] { 0 }, tapY = new float[] { 0 };
            bubble.setOnTouchListener(new View.OnTouchListener() {
                public boolean onTouch(View v, MotionEvent ev) {
                    if (ev.getActionMasked() == MotionEvent.ACTION_UP) {
                        long now = System.currentTimeMillis();
                        float dx = Math.abs(ev.getX() - tapX[0]);
                        float dy = Math.abs(ev.getY() - tapY[0]);
                        if (tapAt[0] > 0 && now - tapAt[0] <= 400
                                && dx <= dp(36) && dy <= dp(36)) {
                            tapAt[0] = 0;                     // 用掉这次双击，避免连环触发
                            if (pic.getDrawable() instanceof BitmapDrawable) {
                                showBigPic(((BitmapDrawable) pic.getDrawable()).getBitmap());
                            }
                        } else {
                            tapAt[0] = now;
                            tapX[0] = ev.getX();
                            tapY[0] = ev.getY();
                        }
                    }
                    return false;
                }
            });
        }
        bubble.addView(bubbleText);

        LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        bubble.setLayoutParams(blp);
        bubble.setOnLongClickListener(new View.OnLongClickListener() {
            public boolean onLongClick(View v) { showMsgMenu(m); return true; }
        });
        row.addView(bubble);
        chatBox.addView(row);
    }

    void scrollToBottom() {
        if (chatScroll == null) return;
        // 分几次滚：刚塞进去时布局还没算完，150 条消息的高度要好几帧才量准
        final int[] delays = new int[] { 0, 200, 600 };
        for (int i = 0; i < delays.length; i++) {
            chatScroll.postDelayed(new Runnable() {
                public void run() {
                    if (stickBottom) chatScroll.fullScroll(View.FOCUS_DOWN);
                }
            }, delays[i]);
        }
    }

    /** 是否已经贴在底部（决定刷新后要不要自动滚下去） */
    boolean isAtBottom() {
        if (chatScroll == null || chatBox == null) return true;
        int vh = chatScroll.getHeight();
        int ch = chatBox.getHeight();
        if (vh <= 0 || ch <= 0) return true;
        return (chatScroll.getScrollY() + vh) >= (ch - dp(28));
    }

    void loadLogs() {
        runBox.setText("加载中…");
        getJson("/api/runlog", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { runBox.setText(err); return; }
                runBox.setText(o.optString("text", "（空）"));
            }
        });
    }

    String join(List<String> ls, String sep) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < ls.size(); i++) {
            if (i > 0) sb.append(sep);
            sb.append(ls.get(i));
        }
        return sb.toString();
    }

    // ================================================== 按钮
    void onAction(String action) {
        if ("selfcheck".equals(action)) { runSelfCheck(); return; }
        if ("qr".equals(action)) { loadQr(); return; }
        if ("sendchat".equals(action)) { doSendChat(); return; }
        if ("webui".equals(action)) {
            toast("WebUI：浏览器打开 " + BASE.replace(":8090", ":5099"));
            return;
        }
        if ("roster".equals(action)) {
            openUrl(BASE + "/roster.html?token=" + TOKEN);
            return;
        }
        if ("roster-copy".equals(action)) {
            try {
                android.content.ClipboardManager cm = (android.content.ClipboardManager)
                        getSystemService(CLIPBOARD_SERVICE);
                cm.setPrimaryClip(android.content.ClipData.newPlainText(
                        "roster", BASE + "/roster.html?token=" + TOKEN));
                toast("花名册链接已复制");
            } catch (Exception e) { toast("复制失败"); }
            return;
        }
        if ("refresh".equals(action)) { loadStatus(); toast("已刷新"); return; }
        if ("ctl-groups".equals(action)) { ctlLoadGroups(); return; }
        if ("ctl-reload".equals(action)) { if (ctlChat != null) ctlLoadRules(ctlChat); return; }
        if ("ctl-save".equals(action)) { ctlSave(); return; }
        if ("ctl-at".equals(action)) { ctlOnlyAt = !ctlOnlyAt; ctlPaint(); return; }
        if ("bl-add".equals(action)) { blAdd(); return; }
        if ("bl-toggle".equals(action)) { blToggleOpen(); return; }
        if ("bl-noop".equals(action)) { return; }
        if ("ctl-len14".equals(action)) { ctlLen = 14; ctlPaint(); return; }
        if ("ctl-len26".equals(action)) { ctlLen = 26; ctlPaint(); return; }
        if ("ctl-len40".equals(action)) { ctlLen = 40; ctlPaint(); return; }
        if ("ctl-rateclear".equals(action)) { ctlRate = -1.0; ctlPaint(); return; }
        if ("ctl-ambientdef".equals(action)) { ctlAmbient = -1.0; ctlPaint(); return; }
        if ("ps-new".equals(action)) { psNew(); return; }
        if ("ps-save".equals(action)) { psSave(); return; }
        if ("ps-read".equals(action)) {
            if (psSel.length() > 0) psRead(psSel); else psStatus.setText("先在列表里点「读」选一个人格");
            return;
        }
        if ("fix".equals(action)) { doFix(); return; }
        if ("stop".equals(action)) {
            confirm("关闭机器人？（会先发一句告别）", "stop");
            return;
        }
        if ("napcat-restart".equals(action)) {
            confirm("重启协议端（SnowLuma）？约 30 秒恢复，期间她不在线。", "napcat-restart");
            return;
        }
        act(action);
    }

    void toggleVoiceMode() {
        sendAsVoice = !sendAsVoice;
        if (voiceToggle != null) {
            voiceToggle.setText(sendAsVoice ? "语音 ✓" : "语音");
            voiceToggle.setTextColor(sendAsVoice ? 0xFFFFFFFF : C_SUB);
            voiceToggle.setBackground(roundBg(sendAsVoice ? C_ACCENT : 0xFF22252A,
                    sendAsVoice ? 0 : C_LINE, 14));
        }
        toast(sendAsVoice ? "发出去的是语音（合成约 6 秒）" : "改回发文字");
    }

    // ================================================== @ 某人 / 引用回复
    boolean isGroup() { return chatTarget != null && chatTarget.startsWith("群"); }

    String enc(String s) {
        try { return URLEncoder.encode(s == null ? "" : s, "UTF-8"); } catch (Exception e) { return ""; }
    }

    /** 输入框上面那排芯片：引用一条 + ＠N 个人，都带 ✕ 能取消。 */
    void paintCompose() {
        if (composeBar == null) return;
        composeBar.removeAllViews();
        boolean any = quoteMsg != null || !atList.isEmpty();
        if (!any) { composeBar.setVisibility(View.GONE); fitChatHeight(); return; }

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        HorizontalScrollView hsv = new HorizontalScrollView(this);
        hsv.setHorizontalScrollBarEnabled(false);
        hsv.addView(row);

        if (quoteMsg != null) {
            String qt = quoteMsg.optString("text");
            String qw = quoteMsg.optString("who");
            String label = "引用 " + (qw.length() > 0 ? qw + "：" : "")
                    + (qt.length() > 40 ? qt.substring(0, 40) + "…" : qt);
            row.addView(chip(label, new Runnable() {
                public void run() { quoteMsg = null; paintCompose(); }
            }));
        }
        for (final JSONObject m : new ArrayList<JSONObject>(atList)) {
            row.addView(chip("＠" + m.optString("name"), new Runnable() {
                public void run() { atList.remove(m); paintCompose(); }
            }));
        }
        composeBar.addView(hsv);
        composeBar.setVisibility(View.VISIBLE);
        fitChatHeight();                    // 多了一条引用/＠，把消息区让出高度
    }

    LinearLayout chip(String label, final Runnable onClose) {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.HORIZONTAL);
        c.setGravity(Gravity.CENTER_VERTICAL);
        c.setBackground(roundBg(C_BTN, C_LINE, 10));
        c.setPadding(dp(10), dp(5), dp(5), dp(5));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, dp(6), 0);
        c.setLayoutParams(lp);

        TextView t = text(label, 12, C_SUB, false);
        t.setSingleLine(true);
        t.setEllipsize(TextUtils.TruncateAt.END);
        t.setMaxWidth(dp(220));
        c.addView(t);

        TextView x = text("✕", 13, C_SUB, false);
        x.setPadding(dp(8), 0, dp(4), 0);
        x.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { onClose.run(); }
        });
        c.addView(x);
        return c;
    }

    void setQuote(JSONObject m) {
        quoteMsg = m;
        paintCompose();
        if (chatInput != null) chatInput.requestFocus();
        toast("发送时会带上这条引用");
    }

    void addAt(JSONObject member) {
        String uid = member.optString("uid");
        if (uid.length() == 0) return;
        for (JSONObject o : atList) {
            if (uid.equals(o.optString("uid"))) { paintCompose(); return; }
        }
        if (atList.size() >= 5) { toast("一次最多 ＠ 5 个人"); return; }
        atList.add(member);
        paintCompose();
    }

    void showMemberPicker(String prefill) {
        if (!isGroup()) { toast("私聊没法 ＠ 人"); return; }

        final LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(18), dp(4), dp(18), 0);

        final EditText search = new EditText(this);
        search.setHint("搜名字 / QQ 号");
        search.setSingleLine(true);
        search.setText(prefill == null ? "" : prefill);
        search.setTextColor(C_TXT);
        search.setHintTextColor(C_SUB);
        box.addView(search);

        final LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        ScrollView sv = new ScrollView(this);
        sv.addView(list, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        box.addView(sv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(330)));

        final AlertDialog dlg = new AlertDialog.Builder(this)
                .setTitle("＠ 谁（群成员）")
                .setView(box)
                .setNegativeButton("取消", null)
                .create();
        dlg.show();

        final Runnable refill = new Runnable() {
            public void run() { fillMemberRows(list, search.getText().toString(), dlg); }
        };
        search.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { }
            public void afterTextChanged(Editable s) { refill.run(); }
        });

        if (chatTarget != null && chatTarget.equals(memberCacheChat) && !memberCache.isEmpty()) {
            refill.run();
            return;
        }
        list.addView(text("拉成员列表…", 13, C_SUB, false));
        final String chat = chatTarget;
        getJson("/api/members?chat=" + enc(chat), new JsonCb() {
            public void on(JSONObject o, String err) {
                if (chat == null || !chat.equals(chatTarget)) return;
                list.removeAllViews();
                if (err != null) { list.addView(text(err, 13, C_BAD, false)); return; }
                JSONArray arr = o == null ? null : o.optJSONArray("items");
                memberCache.clear();
                for (int i = 0; arr != null && i < arr.length(); i++) {
                    JSONObject m = arr.optJSONObject(i);
                    if (m != null) memberCache.add(m);
                }
                memberCacheChat = chat;
                refill.run();
            }
        });
    }

    void fillMemberRows(LinearLayout list, String q, final AlertDialog dlg) {
        list.removeAllViews();
        String query = q == null ? "" : q.trim().toLowerCase();
        int shown = 0;
        for (final JSONObject m : memberCache) {
            String name = m.optString("name");
            String nick = m.optString("nick");
            String uid = m.optString("uid");
            if (query.length() > 0 && !name.toLowerCase().contains(query)
                    && !nick.toLowerCase().contains(query) && !uid.contains(query)) continue;
            if (shown >= 100) break;
            shown++;
            String label = name
                    + (nick.length() > 0 && !nick.equals(name) ? "（" + nick + "）" : "")
                    + "   " + uid;
            TextView row = text(label, 14, C_TXT, false);
            row.setPadding(dp(4), dp(12), dp(4), dp(12));
            row.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    addAt(m);
                    if (dlg != null) dlg.dismiss();
                }
            });
            list.addView(row);
        }
        if (shown == 0) list.addView(text("没有匹配的人", 13, C_SUB, false));
    }

    // ================================================== 表情包（仿 QQ：4 列网格 + 翻页圆点）
    // 图来自她自己那套 /opt/qqbot/stickers（和插件发的是同一批），点一张就发出去。
    static JSONObject stkCache = null;
    static long stkCacheTs = 0;
    final java.util.HashMap<String, Bitmap> stkThumbs = new java.util.HashMap<String, Bitmap>();
    final java.util.HashMap<String, ImageView> stkTileViews = new java.util.HashMap<String, ImageView>();
    LinearLayout stkGridBox, stkFootBox;
    EditText stkSearchBox;
    AlertDialog stkDlg;
    int stkPageNo = 0;
    boolean stkThumbLoading = false;
    static final int STK_COLS = 4;
    static final int STK_ROWS = 4;                            // 4 列 × 4 行：手机屏幕放得下，不用往上划
    static final int STK_PER_PAGE = STK_COLS * STK_ROWS;      // 一页 16 张

    void loadStickerList(final JsonCb cb) {
        long now = System.currentTimeMillis();
        if (stkCache != null && now - stkCacheTs < 120000L) { cb.on(stkCache, null); return; }
        getJson("/api/stickers", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err == null && o != null && o.optJSONArray("items") != null) {
                    stkCache = o;
                    stkCacheTs = System.currentTimeMillis();
                }
                cb.on(o, err);
            }
        });
    }

    /** 群聊黑话编辑（2026-10-30）：一行一条「词 = 说明」，按当前群存 */
    void showSlangEditor() {
        final String t = chatTarget == null ? "" : chatTarget.trim();
        if (!t.startsWith("群")) { toast("先选一个群（黑话是按群存的）"); return; }
        String gid = t.substring(1).trim();
        if (!gid.matches("\\d{5,20}")) { toast("群号没认出来：" + t); return; }
        final String fgid = gid;

        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(2), dp(16), 0);
        box.addView(text("一行一条：词 = 说明（写清什么意思、贬义还是玩笑）", 11, C_SUB, false));
        final EditText ed = new EditText(this);
        ed.setMinLines(10);
        ed.setGravity(Gravity.TOP | Gravity.LEFT);
        ed.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        ed.setTextSize(13);
        ed.setTextColor(C_TXT);
        ed.setHintTextColor(C_SUB);
        ed.setHint("老六 = 阴人、不正面来的人\n答辩 = 大便，贬义，别乱用");
        ed.setBackground(roundBg(C_BTN, C_LINE, 10));
        ed.setPadding(dp(10), dp(8), dp(10), dp(8));
        LinearLayout.LayoutParams elp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(220));
        elp.setMargins(0, dp(6), 0, 0);
        ed.setLayoutParams(elp);
        box.addView(ed);

        final AlertDialog dlg = new AlertDialog.Builder(this)
                .setTitle("群聊黑话 · " + fgid)
                .setView(box)
                .setNegativeButton("关闭", null)
                .setPositiveButton("保存", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) { saveSlang(fgid, ed.getText().toString()); }
                })
                .create();
        dlg.show();

        getJson("/api/slang?gid=" + fgid, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null || o == null) return;
                JSONObject items = o.optJSONObject("items");
                StringBuilder sb = new StringBuilder();
                if (items != null) {
                    java.util.Iterator<String> it = items.keys();
                    while (it.hasNext()) {
                        String k = it.next();
                        sb.append(k).append(" = ").append(items.optString(k)).append("\n");
                    }
                }
                ed.setText(sb.toString());
            }
        });
    }

    void saveSlang(final String gid, String text) {
        JSONObject items = new JSONObject();
        int n = 0;
        for (String ln : (text == null ? "" : text).split("\n")) {
            String s = ln.trim();
            if (s.length() == 0) continue;
            int p = s.indexOf('=');
            if (p < 0) p = s.indexOf('＝');
            if (p <= 0) continue;
            String k = s.substring(0, p).trim();
            String v = s.substring(p + 1).trim();
            if (k.length() == 0 || v.length() == 0) continue;
            try { items.put(k, v); n++; } catch (Exception e) { }
        }
        JSONObject body = new JSONObject();
        try {
            body.put("gid", gid);
            body.put("items", items);
        } catch (Exception e) { }
        final int cnt = n;
        postJsonBody("/api/slang", body, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { toast("保存失败：" + err); return; }
                if (o != null && !o.optBoolean("ok", false)) { toast(o.optString("msg", "保存失败")); return; }
                toast("已保存 " + cnt + " 条（她下条消息就认）");
            }
        });
    }

    void showStickerPicker() {
        if (chatTarget == null || chatTarget.length() == 0) { toast("先选一个会话"); return; }

        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(2), dp(16), 0);

        stkSearchBox = new EditText(this);
        stkSearchBox.setHint("搜情绪 / 画面，比如「生气」「摊手」");
        stkSearchBox.setSingleLine(true);
        stkSearchBox.setTextColor(C_TXT);
        stkSearchBox.setHintTextColor(C_SUB);
        stkSearchBox.setTextSize(14);
        stkSearchBox.setBackground(roundBg(C_BTN, C_LINE, 12));
        stkSearchBox.setPadding(dp(12), dp(8), dp(12), dp(8));
        box.addView(stkSearchBox);

        stkGridBox = new LinearLayout(this);
        stkGridBox.setOrientation(LinearLayout.VERTICAL);
        stkGridBox.setPadding(0, dp(6), 0, 0);
        ScrollView sv = new ScrollView(this);
        sv.addView(stkGridBox, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        box.addView(sv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(308)));

        stkFootBox = new LinearLayout(this);
        stkFootBox.setOrientation(LinearLayout.HORIZONTAL);
        stkFootBox.setGravity(Gravity.CENTER_VERTICAL);
        stkFootBox.setPadding(0, dp(6), 0, 0);
        box.addView(stkFootBox);

        stkSearchBox.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { }
            public void afterTextChanged(Editable s) { stkPageNo = 0; fillStickerGrid(); }
        });

        stkPageNo = 0;
        stkDlg = new AlertDialog.Builder(this)
                .setTitle("表情（长按一张改标签）")
                .setView(box)
                .setNegativeButton("关闭", null)
                .create();
        stkDlg.show();

        stkGridBox.addView(text("拉表情包列表…", 13, C_SUB, false));
        loadStickerList(new JsonCb() {
            public void on(JSONObject o, String err) {
                if (stkGridBox == null) return;
                stkGridBox.removeAllViews();
                if (err != null) { stkGridBox.addView(text(err, 13, C_BAD, false)); return; }
                fillStickerGrid();
            }
        });
    }

    /** 按搜索词过滤（情绪 / 描述 / 文件名 都算） */
    List<JSONObject> stkFiltered() {
        List<JSONObject> out = new ArrayList<JSONObject>();
        if (stkCache == null) return out;
        JSONArray arr = stkCache.optJSONArray("items");
        String q = stkSearchBox == null ? "" : stkSearchBox.getText().toString().trim().toLowerCase();
        for (int i = 0; arr != null && i < arr.length(); i++) {
            JSONObject it = arr.optJSONObject(i);
            if (it == null) continue;
            if (q.length() > 0) {
                String hay = (it.optString("mood") + " " + it.optString("desc")
                        + " " + it.optString("tags") + " " + it.optString("id")).toLowerCase();
                if (!hay.contains(q)) continue;
            }
            out.add(it);
        }
        return out;
    }

    void fillStickerGrid() {
        if (stkGridBox == null) return;
        stkGridBox.removeAllViews();
        stkTileViews.clear();
        final List<JSONObject> list = stkFiltered();
        int pages = Math.max(1, (list.size() + STK_PER_PAGE - 1) / STK_PER_PAGE);
        if (stkPageNo < 0) stkPageNo = 0;
        if (stkPageNo > pages - 1) stkPageNo = pages - 1;

        final List<String> missing = new ArrayList<String>();
        LinearLayout row = null;
        for (int i = 0; i < STK_PER_PAGE; i++) {
            if (i % STK_COLS == 0) {
                row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                stkGridBox.addView(row);
            }
            int idx = stkPageNo * STK_PER_PAGE + i;
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(70), 1f);
            lp.setMargins(dp(3), dp(3), dp(3), dp(3));
            if (idx >= list.size()) {
                View ph = new View(this);          // 占位，别让最后一行的格子被拉宽
                ph.setLayoutParams(lp);
                row.addView(ph);
                continue;
            }
            final JSONObject it = list.get(idx);
            final String id = it.optString("id");
            ImageView iv = new ImageView(this);
            iv.setLayoutParams(lp);
            iv.setScaleType(ImageView.ScaleType.FIT_CENTER);
            iv.setBackground(roundBg(C_BTN, C_LINE, 8));
            iv.setPadding(dp(4), dp(4), dp(4), dp(4));
            iv.setContentDescription(it.optString("mood") + "｜" + it.optString("desc"));
            Bitmap bm = stkThumbs.get(id);
            if (bm != null) iv.setImageBitmap(bm); else missing.add(id);
            iv.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) {
                    sendSticker(id);
                    if (stkDlg != null) stkDlg.dismiss();   // 和手机 QQ 一样：点了就发、面板收起
                }
            });
            // 2026-10-30：长按改标签（她自己按标签判断什么时候发哪张）
            iv.setOnLongClickListener(new View.OnLongClickListener() {
                public boolean onLongClick(View v) {
                    stkEditTags(it);
                    return true;
                }
            });
            stkTileViews.put(id, iv);
            row.addView(iv);
        }
        paintStkFoot(pages, list.size());
        if (missing.size() > 0) loadStkThumbs(missing);
    }

    /** 长按一张表情包：改 情绪/画面/场合/自定义标签（她按标签挑图，2026-10-30） */
    void stkEditTags(final JSONObject it) {
        final String id = it.optString("id");
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(18), dp(2), dp(18), 0);

        box.addView(text(id, 11, C_SUB, false));

        final EditText eMood = plInput("情绪，比如 生气 / 开心");
        eMood.setText(it.optString("mood"));
        box.addView(eMood);

        final EditText eDesc = plInput("画面：一句话说清图上发生了什么");
        eDesc.setText(it.optString("desc"));
        box.addView(eDesc);

        final EditText eScene = plInput("场合：什么情况下发，比如 被催更时");
        eScene.setText(it.optString("scene"));
        box.addView(eScene);

        final EditText eTags = plInput("标签（逗号分隔，她按这个挑图）\n比如：米饭,吃饭,投喂");
        eTags.setText(it.optString("tags"));
        box.addView(eTags);

        new AlertDialog.Builder(this)
                .setTitle("改标签")
                .setView(box)
                .setNegativeButton("取消", null)
                .setPositiveButton("保存", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) {
                        JSONObject body = new JSONObject();
                        try {
                            body.put("id", id);
                            body.put("mood", eMood.getText().toString().trim());
                            body.put("desc", eDesc.getText().toString().trim());
                            body.put("scene", eScene.getText().toString().trim());
                            body.put("tags", eTags.getText().toString().trim());
                        } catch (Exception e) { }
                        postJsonBody("/api/stickers/tags", body, new JsonCb() {
                            public void on(JSONObject o, String err) {
                                if (err != null) { toast("保存失败：" + err); return; }
                                if (o != null && !o.optBoolean("ok", false)) {
                                    toast(o.optString("msg", "保存失败"));
                                    return;
                                }
                                toast("标签已保存");
                                stkCache = null;
                                stkCacheTs = 0;
                                loadStickerList(new JsonCb() {
                                    public void on(JSONObject o2, String e2) { fillStickerGrid(); }
                                });
                            }
                        });
                    }
                })
                .show();
    }

    void paintStkFoot(final int pages, int total) {
        if (stkFootBox == null) return;
        stkFootBox.removeAllViews();

        TextView prev = text("‹", 16, C_SUB, true);
        prev.setGravity(Gravity.CENTER);
        prev.setBackground(roundBg(C_BTN, C_LINE, 10));
        prev.setLayoutParams(new LinearLayout.LayoutParams(dp(34), dp(30)));
        prev.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { stkGoPage(stkPageNo - 1); }
        });
        stkFootBox.addView(prev);

        LinearLayout dots = new LinearLayout(this);
        dots.setOrientation(LinearLayout.HORIZONTAL);
        dots.setGravity(Gravity.CENTER);
        dots.setLayoutParams(new LinearLayout.LayoutParams(0, dp(30), 1f));
        for (int i = 0; i < pages && i < 24; i++) {
            View d = new View(this);
            int sz = i == stkPageNo ? dp(9) : dp(7);
            LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(sz, sz);
            dlp.setMargins(dp(3), 0, dp(3), 0);
            d.setLayoutParams(dlp);
            d.setBackground(roundBg(i == stkPageNo ? C_ACCENT : C_LINE, 0, 4));
            final int target = i;
            d.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { stkGoPage(target); }
            });
            dots.addView(d);
        }
        stkFootBox.addView(dots);

        TextView next = text("›", 16, C_SUB, true);
        next.setGravity(Gravity.CENTER);
        next.setBackground(roundBg(C_BTN, C_LINE, 10));
        next.setLayoutParams(new LinearLayout.LayoutParams(dp(34), dp(30)));
        next.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { stkGoPage(stkPageNo + 1); }
        });
        stkFootBox.addView(next);

        TextView info = text((stkPageNo + 1) + "/" + pages + " 页 · " + total + " 张", 11, C_SUB, false);
        info.setPadding(dp(8), 0, 0, 0);
        info.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        stkFootBox.addView(info);
    }

    void stkGoPage(int p) {
        List<JSONObject> list = stkFiltered();
        int pages = Math.max(1, (list.size() + STK_PER_PAGE - 1) / STK_PER_PAGE);
        if (p < 0) p = 0;
        if (p > pages - 1) p = pages - 1;
        if (p == stkPageNo) return;
        stkPageNo = p;
        fillStickerGrid();
    }

    /** 一批缩略图一起取（一页 20 张，不能一张一请求） */
    void loadStkThumbs(final List<String> ids) {
        if (stkThumbLoading || ids.isEmpty()) return;
        stkThumbLoading = true;
        StringBuilder sb = new StringBuilder();
        for (String id : ids) {
            if (sb.length() > 0) sb.append(",");
            sb.append(id);
        }
        getJson("/api/stickers/thumbs?ids=" + sb.toString(), new JsonCb() {
            public void on(JSONObject o, String err) {
                stkThumbLoading = false;
                if (err != null || o == null) return;
                JSONArray arr = o.optJSONArray("items");
                for (int i = 0; arr != null && i < arr.length(); i++) {
                    JSONObject it = arr.optJSONObject(i);
                    if (it == null) continue;
                    String id = it.optString("id");
                    try {
                        byte[] raw = android.util.Base64.decode(it.optString("b64"),
                                android.util.Base64.DEFAULT);
                        Bitmap bm = BitmapFactory.decodeByteArray(raw, 0, raw.length);
                        if (bm == null) continue;
                        stkThumbs.put(id, bm);
                        ImageView iv = stkTileViews.get(id);
                        if (iv != null) iv.setImageBitmap(bm);
                    } catch (Exception e) { }
                }
            }
        });
    }

    void sendSticker(final String id) {
        if (chatTarget == null || chatTarget.length() == 0) { toast("先选一个会话"); return; }
        final String chat = chatTarget;
        final JSONArray atArr = new JSONArray();
        for (JSONObject m : atList) {
            JSONObject o = new JSONObject();
            try {
                o.put("uid", m.optString("uid"));
                o.put("name", m.optString("name"));
                atArr.put(o);
            } catch (Exception e) { }
        }
        final boolean hasQuote = quoteMsg != null;
        final JSONObject quoteObj = new JSONObject();
        if (hasQuote) {
            try {
                quoteObj.put("who", quoteMsg.optString("who"));
                quoteObj.put("text", quoteMsg.optString("text"));
                quoteObj.put("ts", quoteMsg.optString("ts"));
                quoteObj.put("dt", quoteMsg.optString("dt"));
            } catch (Exception e) { }
        }
        toast("发表情包…");
        pool.execute(new Runnable() {
            public void run() {
                String err = null;
                JSONObject obj = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api("/api/stickers/send")).openConnection();
                    c.setDoOutput(true);
                    c.setRequestMethod("POST");
                    c.setRequestProperty("Content-Type", "application/json");
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(150000);
                    JSONObject body = new JSONObject();
                    body.put("chat", chat);
                    JSONArray ids = new JSONArray();
                    ids.put(id);
                    body.put("ids", ids);
                    if (atArr.length() > 0) body.put("at", atArr);
                    if (hasQuote) body.put("quote", quoteObj);
                    c.getOutputStream().write(body.toString().getBytes("UTF-8"));
                    int code = c.getResponseCode();
                    InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
                    obj = new JSONObject(read(is));
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final JSONObject fo = obj;
                final String fe = err;
                ui.post(new Runnable() {
                    public void run() {
                        if (fe != null) { toast(fe); return; }
                        boolean ok = fo != null && fo.optBoolean("ok");
                        toast(ok ? "已发表情包" : (fo == null ? "发送失败" : fo.optString("msg", "发送失败")));
                        if (ok) {
                            atList.clear();
                            quoteMsg = null;
                            paintCompose();
                            ui.postDelayed(new Runnable() {
                                public void run() { loadMessages(); loadConversations(); }
                            }, 800);
                        }
                    }
                });
            }
        });
    }

    /** 长按气泡弹出的菜单 */
    void showMsgMenu(final JSONObject m) {
        boolean canAt = isGroup() && m.optString("who").length() > 0 && !"我".equals(m.optString("who"));
        final List<String> items = new ArrayList<String>();
        items.add("引用回复");
        items.add("复制文字");
        if (canAt) items.add("＠" + m.optString("who"));
        new AlertDialog.Builder(this)
                .setItems(items.toArray(new String[0]), new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int which) {
                        if (which == 0) setQuote(m);
                        else if (which == 1) copyText(m.optString("text"));
                        else showMemberPicker(m.optString("who"));
                    }
                })
                .show();
    }

    void copyText(String s) {
        try {
            android.content.ClipboardManager cm = (android.content.ClipboardManager)
                    getSystemService(CLIPBOARD_SERVICE);
            cm.setPrimaryClip(android.content.ClipData.newPlainText("msg", s == null ? "" : s));
            toast("已复制");
        } catch (Exception e) { toast("复制失败"); }
    }

    void doSendChat() {
        if (chatTarget == null || chatTarget.length() == 0) { toast("先选一个会话"); return; }
        final String txt = chatInput.getText().toString().trim();
        if (txt.length() == 0) { toast("内容为空"); return; }

        // 先把 @ / 引用快照下来，再进后台线程（别在线程里摸 UI 状态）
        final JSONArray atArr = new JSONArray();
        for (JSONObject m : atList) {
            JSONObject o = new JSONObject();
            try {
                o.put("uid", m.optString("uid"));
                o.put("name", m.optString("name"));
                atArr.put(o);
            } catch (Exception e) { }
        }
        final boolean hasQuote = quoteMsg != null;
        final JSONObject quoteObj = new JSONObject();
        if (hasQuote) {
            try {
                quoteObj.put("who", quoteMsg.optString("who"));
                quoteObj.put("text", quoteMsg.optString("text"));
                quoteObj.put("ts", quoteMsg.optString("ts"));
                quoteObj.put("dt", quoteMsg.optString("dt"));
            } catch (Exception e) { }
        }

        toast("发送中…");
        if (sendAsVoice && (atList.size() > 0 || quoteMsg != null)) {
            toast("语音消息不带 ＠ / 引用，这次只发语音");
        }
        pool.execute(new Runnable() {
            public void run() {
                String err = null;
                JSONObject obj = null;
                HttpURLConnection c = null;
                try {
                    c = (HttpURLConnection) new URL(api("/api/send")).openConnection();
                    c.setDoOutput(true);
                    c.setRequestMethod("POST");
                    c.setRequestProperty("Content-Type", "application/json");
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(150000);
                    JSONObject body = new JSONObject();
                    body.put("chat", chatTarget);
                    body.put("text", txt);
                    body.put("voice", sendAsVoice);
                    if (!sendAsVoice && atArr.length() > 0) body.put("at", atArr);
                    if (!sendAsVoice && hasQuote) body.put("quote", quoteObj);
                    c.getOutputStream().write(body.toString().getBytes("UTF-8"));
                    int code = c.getResponseCode();
                    InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
                    obj = new JSONObject(read(is));
                } catch (Exception e) {
                    err = "连不上服务器";
                } finally {
                    if (c != null) c.disconnect();
                }
                final JSONObject fo = obj;
                final String fe = err;
                ui.post(new Runnable() {
                    public void run() {
                        if (fe != null) { toast(fe); return; }
                        boolean ok = fo != null && fo.optBoolean("ok");
                        toast(ok ? "已发送" : (fo == null ? "发送失败" : fo.optString("msg", "发送失败")));
                        if (ok) {
                            chatInput.setText("");
                            if (!sendAsVoice) {      // 语音没带出去，别把芯片清了
                                atList.clear();
                                quoteMsg = null;
                                paintCompose();
                            }
                            ui.postDelayed(new Runnable() {
                                public void run() { loadMessages(); loadConversations(); }
                            }, 800);
                        }
                    }
                });
            }
        });
    }

    void confirm(String msg, final String action) {
        new AlertDialog.Builder(this)
                .setMessage(msg)
                .setPositiveButton("确定", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int w) { act(action); }
                })
                .setNegativeButton("取消", null)
                .show();
    }

    void act(final String name) {
        toast("执行中…");
        postAction(name, new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { toast(err); return; }
                String msg = o.optString("msg", "已执行");
                toast(msg.length() > 70 ? msg.substring(0, 70) : msg);
                ui.postDelayed(new Runnable() { public void run() { loadStatus(); } }, 1500);
                if ("napcat-restart".equals(name)) {
                    ui.postDelayed(new Runnable() {
                        public void run() { if (curTab == 2) loadQr(); }
                    }, 20000);
                }
            }
        });
    }

    void doFix() {
        getJson("/api/selfcheck", new JsonCb() {
            public void on(JSONObject o, String err) {
                if (err != null) { toast(err); return; }
                StringBuilder bad = new StringBuilder();
                try {
                    JSONArray items = o.optJSONArray("items");
                    for (int i = 0; items != null && i < items.length(); i++) {
                        JSONObject it = items.getJSONObject(i);
                        if (!it.optBoolean("ok")) {
                            if (bad.length() > 0) bad.append("、");
                            bad.append(it.optString("name"));
                        }
                    }
                } catch (Exception e) { }
                if (bad.length() == 0) { toast("都正常，不用修"); return; }
                String s = bad.toString();
                if (s.contains("fishbot")) { act("start"); return; }
                if (s.contains("协议端") || s.contains("OneBot") || s.contains("容器")) { act("napcat-restart"); return; }
                toast("需要手动处理：" + s);
            }
        });
    }
}





