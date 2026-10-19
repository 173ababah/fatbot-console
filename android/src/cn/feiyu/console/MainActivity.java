package cn.feiyu.console;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.TextUtils;
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
    static final String BASE = "http://your-server.example.com:8090";
    static final String TOKEN = "CHANGE_ME_CONSOLE_TOKEN";

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

    LinearLayout root, pageHome, pageCtl, pageSelf, pageQr, pageLogs;
    final TextView[] tabViews = new TextView[5];

    // 机器人控件（每群）
    Spinner ctlSpinner;
    Button ctlAtBtn;
    SeekBar ctlSpeedBar, ctlRateBar;
    TextView ctlSpeedVal, ctlRateVal, ctlLenVal, ctlStatus, ctlGroupTip;
    Button ctlLen14, ctlLen26, ctlLen40, ctlRateClear;
    String ctlChat = null;
    boolean ctlOnlyAt = false;
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
    final String[] QUICK = {"我回来啦", "我去吃饭了", "晚安", "在忙吗"};
    LinearLayout convCard, chatCardView;
    String chatTarget = null;      // 会话 key：「群 123456」/「私聊 789」
    String chatTitle = null;       // 显示用的名字（群名 / 好友昵称）
    Runnable chatPoll = null;
    ScrollView rootScroll;
    boolean logMsg = true;      // true=聊天气泡  false=运行日志
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
    void buildUi() {
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
        String[] names = {"状态", "控件", "自检", "扫码", "日志"};
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
        root.addView(pageHome);
        root.addView(pageCtl);
        root.addView(pageSelf);
        root.addView(pageQr);
        root.addView(pageLogs);

        scroll.addView(root, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        rootScroll = scroll;
        setContentView(scroll);
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

        LinearLayout c4 = card();
        c4.addView(text("看门狗", 16, C_TXT, true));
        vDog = text("每 3 分钟检查一次：掉线自动重启协议端。", 13, C_SUB, false);
        vDog.setPadding(0, dp(6), 0, 0);
        c4.addView(vDog);
        page.addView(c4);
        return page;
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

        // 事件
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
                ctlRateVal.setText(p + "%");
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
        return page;
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

    void ctlPaint() {
        if (ctlAtBtn != null) {
            ctlAtBtn.setText(ctlOnlyAt ? "已开启" : "关闭");
            ctlAtBtn.setBackground(roundBg(ctlOnlyAt ? C_ACCENT : C_BTN, C_LINE, 14));
            ctlAtBtn.setTextColor(ctlOnlyAt ? 0xFFFFFFFF : C_TXT);
        }
        if (ctlSpeedVal != null) {
            int p = (int) Math.round(ctlSpeed * 100) - 30;
            if (p < 0) p = 0;
            if (ctlSpeedBar.getProgress() != p) ctlSpeedBar.setProgress(p);
            ctlSpeedVal.setText(String.format("%.2fx", ctlSpeed));
        }
        if (ctlRateVal != null) {
            if (ctlRate < 0) {
                ctlRateVal.setText("（跟全局）");
                ctlRateBar.setEnabled(false);
            } else {
                ctlRateBar.setEnabled(true);
                int p = (int) Math.round(ctlRate * 100);
                if (ctlRateBar.getProgress() != p) ctlRateBar.setProgress(p);
                ctlRateVal.setText(p + "%");
            }
        }
        if (ctlLenVal != null) ctlLenVal.setText("当前：" + ctlLen + " 字");
        Button[] lens = {ctlLen14, ctlLen26, ctlLen40};
        int[] vals = {14, 26, 40};
        for (int i = 0; i < lens.length; i++) {
            if (lens[i] == null) continue;
            boolean on = (ctlLen == vals[i]);
            lens[i].setBackground(roundBg(on ? C_ACCENT_D : C_BTN, on ? 0 : C_LINE, 14));
            lens[i].setTextColor(on ? 0xFFDFE7FF : C_TXT);
        }
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

        // 主动发送：输入框 + 发送按钮
        LinearLayout sendRow = new LinearLayout(this);
        sendRow.setOrientation(LinearLayout.HORIZONTAL);
        sendRow.setGravity(Gravity.CENTER_VERTICAL);
        sendRow.setPadding(0, dp(8), 0, 0);
        chatInput = new EditText(this);
        chatInput.setHint("以她的身份发一句话…");
        chatInput.setHintTextColor(C_SUB);
        chatInput.setTextColor(C_TXT);
        chatInput.setTextSize(14);
        chatInput.setBackground(roundBg(0xFF22252A, C_LINE, 12));
        chatInput.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        ilp.setMargins(0, 0, dp(8), 0);
        chatInput.setLayoutParams(ilp);
        chatInput.setMaxLines(3);
        sendRow.addView(chatInput);
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
        int h = (int) (screen * 0.52f);        // 屏幕高度的 52%
        int min = dp(300);
        int max = dp(560);
        if (h < min) h = min;
        if (h > max) h = max;
        return h;
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

    void toast(String s) {
        Toast.makeText(this, s, Toast.LENGTH_SHORT).show();
    }

    // ================================================== 标签切换
    void switchTab(int idx) {
        curTab = idx;
        LinearLayout[] pages = {pageHome, pageCtl, pageSelf, pageQr, pageLogs};
        for (int i = 0; i < pages.length; i++) {
            pages[i].setVisibility(i == idx ? View.VISIBLE : View.GONE);
            paintTab(tabViews[i], i == idx);
        }
        if (idx == 1) { if (ctlGids.isEmpty()) ctlLoadGroups(); }
        if (idx == 2) runSelfCheck();
        if (idx == 3) { loadQr(); startQrTimer(); } else stopQrTimer();
        if (idx == 4) {
            if (logMsg) { showConvList(); loadConversations(); }
            else { if (convCard != null) convCard.setVisibility(View.GONE);
                   if (chatCardView != null) chatCardView.setVisibility(View.GONE);
                   View rc = findRunCard(); if (rc != null) rc.setVisibility(View.VISIBLE);
                   loadLogs(); }
            startChatPoll();
        } else stopChatPoll();
    }

    void paintTab(TextView t, boolean on) {
        t.setBackground(roundBg(on ? C_ACCENT_D : C_CARD, on ? 0 : C_LINE, 12));
        t.setTextColor(on ? 0xFFDFE7FF : C_SUB);
    }

    void startQrTimer() {
        stopQrTimer();
        qrTimer = new Runnable() {
            public void run() {
                if (curTab == 2) { loadQr(); ui.postDelayed(this, 20000); }
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
        if (convCard != null) convCard.setVisibility(View.GONE);
        if (chatCardView != null) chatCardView.setVisibility(View.VISIBLE);
        setChatTitle(chatTitle);
        loadMessages();
        startChatPoll();
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

    void renderMessages(JSONArray arr) {
        chatBox.removeAllViews();
        if (arr == null || arr.length() == 0) {
            chatBox.addView(text("这个会话最近没有消息", 13, C_SUB, false));
            return;
        }
        for (int i = 0; i < arr.length(); i++) {
            try { addBubble(arr.getJSONObject(i)); } catch (Exception e) { }
        }
    }

    void loadMessages() {
        String key = "msg_" + (chatTarget == null ? "" : chatTarget);
        String cached = cacheGet(key);
        boolean painted = false;
        if (cached.length() > 10) {
            try {
                renderMessages(new JSONObject(cached).optJSONArray("messages"));
                painted = true;
            } catch (Exception e) { }
        }
        if (!painted) {
            chatBox.removeAllViews();
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
                renderMessages(o.optJSONArray("messages"));
                cachePut("msg_" + (chatTarget == null ? "" : chatTarget), o.toString());
                String t = o.optString("title");
                if (t != null && t.length() > 0) { chatTitle = t; setChatTitle(t); }
                if (wasBottom) scrollToBottom();   // 翻上去看历史时别被硬拽回底部
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

    void addBubble(JSONObject m) {
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

        TextView bubble = text(txt, 14, out ? 0xFFFFFFFF : C_TXT, false);
        bubble.setPadding(dp(12), dp(9), dp(12), dp(9));
        bubble.setTextIsSelectable(true);
        bubble.setMaxWidth((int) (getResources().getDisplayMetrics().widthPixels * 0.74f));
        bubble.setBackground(roundBg(out ? 0xFF3F6AEF : 0xFF22252A, out ? 0 : C_LINE, 14));
        LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        bubble.setLayoutParams(blp);
        row.addView(bubble);
        chatBox.addView(row);
    }

    void scrollToBottom() {
        if (chatScroll == null) return;
        chatScroll.post(new Runnable() {
            public void run() { chatScroll.fullScroll(View.FOCUS_DOWN); }
        });
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
        if ("refresh".equals(action)) { loadStatus(); toast("已刷新"); return; }
        if ("ctl-groups".equals(action)) { ctlLoadGroups(); return; }
        if ("ctl-reload".equals(action)) { if (ctlChat != null) ctlLoadRules(ctlChat); return; }
        if ("ctl-save".equals(action)) { ctlSave(); return; }
        if ("ctl-at".equals(action)) { ctlOnlyAt = !ctlOnlyAt; ctlPaint(); return; }
        if ("ctl-len14".equals(action)) { ctlLen = 14; ctlPaint(); return; }
        if ("ctl-len26".equals(action)) { ctlLen = 26; ctlPaint(); return; }
        if ("ctl-len40".equals(action)) { ctlLen = 40; ctlPaint(); return; }
        if ("ctl-rateclear".equals(action)) { ctlRate = -1.0; ctlPaint(); return; }
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

    void doSendChat() {
        if (chatTarget == null || chatTarget.length() == 0) { toast("先选一个会话"); return; }
        final String txt = chatInput.getText().toString().trim();
        if (txt.length() == 0) { toast("内容为空"); return; }
        toast("发送中…");
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
