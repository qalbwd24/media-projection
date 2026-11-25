# 媒体投射工具

在 Windows 上通过蓝牙查看和控制 Android 手机上的媒体播放。支持播放、暂停、切歌、媒体音量和进度跳转。Windows 端可收至托盘，也可把可交互的曲目小窗停靠在任务栏。

## 安装

在 [GitHub Releases](https://github.com/qalbwd24/media-projection/releases/latest) 下载两端的安装包。

1. **Android 13 或更新版本**。安装 APK，允许「附近的设备」和「通知访问」。在系统蓝牙设置中将电脑与手机配对，然后在应用中选择这台电脑。
2. **Windows 11 x64**。将 ZIP 解压到固定位置。双击 `MediaProjection.exe`，或先运行「安装到开始菜单.cmd」。在主界面点击「切换手机」，选择已配对的手机。

进度显示和跳转取决于播放器提供的媒体会话能力。详见 [Windows 使用说明](windows/使用说明.md) 和 [Android 使用与构建说明](ble-prototype/README.md)。

## 快捷键

| 按键 | 操作 |
| --- | --- |
| Caps + ↑ / ↓ | 音量加 / 减，支持长按连发 |
| Caps + ← / → | 上一曲 / 下一曲 |
| Caps + 空格 | 播放 / 暂停 |

切歌和播放暂停在短按松开时执行。按住超过 350 毫秒再松开可以取消。

## 构建

在 Windows 上使用 PowerShell 7 运行 `.\Build-Windows.ps1 -Package`。输出在 `dist/`。Android 构建使用 JDK 17 或更新版本、Android SDK Platform 36 和 Build-Tools 36.0.0，详见 [Android 构建说明](ble-prototype/README.md)。

## 数据与连接

配对并选择电脑后，手机端通过加密 BLE 提供媒体状态读取和受支持的控制。通知访问用于发现系统媒体会话。应用的数据接口包含播放状态、曲名、时长、进度和媒体音量。两端使用蓝牙直连。
