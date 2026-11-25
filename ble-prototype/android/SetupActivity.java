package com.weilin.phonemediable;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.NotificationManager;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.content.ComponentName;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.Comparator;

public final class SetupActivity extends Activity {
    static final String PREFS = "media_bridge";
    static final String PC_ADDRESS = "pc_address";
    static final String PC_NAME = "pc_name";
    private TextView state;
    private Button bluetoothPermission;
    private Button notificationAccess;
    private Button chooseComputer;
    private Button disconnect;
    private SharedPreferences prefs;

    @Override public void onCreate(Bundle savedState) {
        super.onCreate(savedState);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        int pad = (int) (24 * getResources().getDisplayMetrics().density);
        layout.setPadding(pad, pad, pad, pad);
        TextView heading = new TextView(this);
        heading.setText("媒体投射工具");
        heading.setTextSize(27);
        layout.addView(heading);
        state = new TextView(this);
        state.setTextSize(16);
        state.setPadding(0, pad, 0, pad);
        layout.addView(state);
        bluetoothPermission = button(layout, "允许附近的设备", () -> requestPermissions(new String[] {
            Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_ADVERTISE
        }, 1));
        notificationAccess = button(layout, "授予媒体访问所需的通知权限", () ->
            startActivity(new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)));
        chooseComputer = button(layout, "选择已配对的电脑", this::pickComputer);
        disconnect = button(layout, "停用蓝牙遥控", () -> {
            prefs.edit().remove(PC_ADDRESS).remove(PC_NAME).apply();
            render();
        });
        TextView details = new TextView(this);
        details.setText("只向选定的电脑提供曲名、播放状态和媒体音量。请在系统蓝牙设置中完成配对。授权一次后可以退出这个页面。");
        details.setPadding(0, pad, 0, 0);
        layout.addView(details);
        setContentView(layout);
    }

    @Override protected void onResume() {
        super.onResume();
        render();
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        render();
    }

    private Button button(LinearLayout parent, String title, Runnable action) {
        Button button = new Button(this);
        button.setText(title);
        button.setOnClickListener(view -> action.run());
        parent.addView(button);
        return button;
    }

    private void render() {
        boolean bt = hasBluetoothPermission(this);
        boolean listener = getSystemService(NotificationManager.class).isNotificationListenerAccessGranted(
            new ComponentName(this, MediaListener.class));
        String computer = prefs.getString(PC_NAME, "");
        state.setText(!bt ? "等待蓝牙授权" : !listener ? "等待媒体访问授权" : computer.isEmpty()
            ? "请选择已配对的电脑" : "已启用 · " + computer + "\n" + MediaListener.status());
        bluetoothPermission.setVisibility(bt ? View.GONE : View.VISIBLE);
        notificationAccess.setVisibility(listener ? View.GONE : View.VISIBLE);
        chooseComputer.setEnabled(bt && listener);
        disconnect.setVisibility(computer.isEmpty() ? View.GONE : View.VISIBLE);
    }

    private void pickComputer() {
        BluetoothAdapter adapter = getSystemService(BluetoothManager.class).getAdapter();
        if (adapter == null || !adapter.isEnabled()) {
            startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS));
            return;
        }
        ArrayList<BluetoothDevice> devices = new ArrayList<>(adapter.getBondedDevices());
        devices.sort(Comparator.comparing(SetupActivity::name, String.CASE_INSENSITIVE_ORDER));
        if (devices.isEmpty()) {
            new AlertDialog.Builder(this).setMessage("请先在系统蓝牙设置中配对电脑。")
                .setPositiveButton("打开蓝牙设置", (dialog, index) ->
                    startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS)))
                .setNegativeButton("取消", null).show();
            return;
        }
        String[] names = devices.stream().map(device -> name(device) + " · " + device.getAddress()).toArray(String[]::new);
        new AlertDialog.Builder(this).setTitle("允许哪台电脑遥控音乐？").setItems(names, (dialog, index) -> {
            BluetoothDevice device = devices.get(index);
            prefs.edit().putString(PC_ADDRESS, device.getAddress()).putString(PC_NAME, name(device)).apply();
            render();
        }).setNegativeButton("取消", null).show();
    }

    private static String name(BluetoothDevice device) {
        String label = device.getName();
        return label == null || label.isBlank() ? "未命名设备" : label;
    }

    static boolean hasBluetoothPermission(android.content.Context context) {
        return context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
            && context.checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE) == PackageManager.PERMISSION_GRANTED;
    }
}
