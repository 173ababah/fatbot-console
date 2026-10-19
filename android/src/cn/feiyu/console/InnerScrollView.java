package cn.feiyu.console;

import android.content.Context;
import android.view.MotionEvent;
import android.widget.ScrollView;

/**
 * 嵌在外层 ScrollView 里的滚动区：抢到手势后告诉父容器不要拦截，
 * 否则手指一滑就被外层滚走了，里面的滚动条看起来"滚不动"。
 */
public class InnerScrollView extends ScrollView {

    public InnerScrollView(Context c) {
        super(c);
        setNestedScrollingEnabled(true);
        setVerticalScrollBarEnabled(true);
        setScrollbarFadingEnabled(false);
    }

    @Override
    public boolean onInterceptTouchEvent(MotionEvent e) {
        if (e.getActionMasked() == MotionEvent.ACTION_DOWN) {
            if (getParent() != null) getParent().requestDisallowInterceptTouchEvent(true);
        }
        return super.onInterceptTouchEvent(e);
    }

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        if (e.getActionMasked() == MotionEvent.ACTION_DOWN) {
            if (getParent() != null) getParent().requestDisallowInterceptTouchEvent(true);
        }
        return super.onTouchEvent(e);
    }
}
