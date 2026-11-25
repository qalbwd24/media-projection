package com.weilin.phonemediable;

import android.bluetooth.BluetoothAdapter;
import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSession;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.service.notification.NotificationListenerService;
import android.util.Log;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import org.json.JSONException;
import org.json.JSONObject;

public final class MediaListener extends NotificationListenerService {
    private static final String TAG = "PhoneMediaBle";
    private static final String VOLUME_CHANGED = "android.media.VOLUME_CHANGED_ACTION";
    private static volatile String serviceStatus = "等待系统连接媒体服务";
    final Handler handler = new Handler(Looper.getMainLooper());
    private final Map<MediaSession.Token, MediaController> watched = new HashMap<>();
    private final MediaSessionManager.OnActiveSessionsChangedListener sessionsChanged = this::watchSessions;
    private List<MediaController> sessions = List.of();
    private MediaController selected;
    private AudioManager audio;
    private MediaSessionManager manager;
    private SharedPreferences prefs;
    private boolean listenerReady;
    private boolean watchingMedia;
    private GattEndpoint endpoint;
    private String allowedAddress = "";
    private String title = "";
    private String mediaId;
    private int duration;
    private boolean hasSession;
    private boolean isPlaying;
    private int volume = -1;
    private int minimum;
    private int maximum;
    private int revision = 1;
    private int trackRevision = 1;
    private boolean changed;
    private byte[] lastProgress;
    private long progressAt;

    private final SharedPreferences.OnSharedPreferenceChangeListener choiceChanged = (store, key) -> refreshEndpoint();
    private final MediaController.Callback mediaChanged = new MediaController.Callback() {
        @Override public void onMetadataChanged(MediaMetadata metadata) { publishPlayback(true); }
        @Override public void onPlaybackStateChanged(PlaybackState state) { publishPlayback(false); }
        @Override public void onSessionDestroyed() { reloadSessions(); }
    };
    private final AudioDeviceCallback routeChanged = new AudioDeviceCallback() {
        @Override public void onAudioDevicesAdded(AudioDeviceInfo[] devices) { publishVolume(true); }
        @Override public void onAudioDevicesRemoved(AudioDeviceInfo[] devices) { publishVolume(true); }
    };
    private final BroadcastReceiver radioEvents = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (BluetoothAdapter.ACTION_STATE_CHANGED.equals(intent.getAction())) refreshEndpoint();
        }
    };
    private final BroadcastReceiver volumeEvents = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (VOLUME_CHANGED.equals(intent.getAction()) && publishVolume(false)) handler.removeCallbacks(volumeFallback);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        audio = getSystemService(AudioManager.class);
        manager = getSystemService(MediaSessionManager.class);
        prefs = getSharedPreferences(SetupActivity.PREFS, MODE_PRIVATE);
        prefs.registerOnSharedPreferenceChangeListener(choiceChanged);
        registerReceiver(radioEvents, new IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED), RECEIVER_EXPORTED);
    }

    @Override public void onListenerConnected() {
        if (listenerReady) return;
        listenerReady = true;
        refreshEndpoint();
    }

    private void startWatchingMedia() {
        if (!listenerReady || watchingMedia) return;
        ComponentName component = new ComponentName(this, MediaListener.class);
        manager.addOnActiveSessionsChangedListener(sessionsChanged, component, handler);
        audio.registerAudioDeviceCallback(routeChanged, handler);
        registerReceiver(volumeEvents, new IntentFilter(VOLUME_CHANGED), RECEIVER_EXPORTED);
        watchingMedia = true;
        reloadSessions();
    }

    @Override public void onListenerDisconnected() {
        stopListening();
        setStatus("等待系统重新连接媒体服务");
        requestRebind(new ComponentName(this, MediaListener.class));
    }

    @Override public void onDestroy() {
        stopListening();
        unregisterReceiver(radioEvents);
        prefs.unregisterOnSharedPreferenceChangeListener(choiceChanged);
        super.onDestroy();
    }

    private void stopListening() {
        listenerReady = false;
        stopWatchingMedia();
        refreshEndpoint();
    }

    private void stopWatchingMedia() {
        if (!watchingMedia) return;
        watchingMedia = false;
        manager.removeOnActiveSessionsChangedListener(sessionsChanged);
        audio.unregisterAudioDeviceCallback(routeChanged);
        unregisterReceiver(volumeEvents);
        handler.removeCallbacks(playbackFallback);
        handler.removeCallbacks(readAfterVolume);
        handler.removeCallbacks(volumeFallback);
        for (MediaController controller : watched.values()) controller.unregisterCallback(mediaChanged);
        watched.clear();
        sessions = List.of();
        selected = null;
    }

    private void reloadSessions() {
        if (watchingMedia) {
            watchSessions(activeSessions());
        }
    }

    private List<MediaController> activeSessions() {
        if (!listenerReady) return List.of();
        try {
            return manager.getActiveSessions(new ComponentName(this, MediaListener.class));
        } catch (SecurityException error) {
            return List.of();
        }
    }

    private void watchSessions(List<MediaController> list) {
        if (!watchingMedia) return;
        sessions = list == null ? List.of() : list;
        Map<MediaSession.Token, MediaController> removed = new HashMap<>(watched);
        for (MediaController controller : sessions) {
            MediaSession.Token token = controller.getSessionToken();
            if (!watched.containsKey(token)) {
                watched.put(token, controller);
                controller.registerCallback(mediaChanged, handler);
            }
            removed.remove(token);
        }
        for (MediaSession.Token token : removed.keySet()) watched.remove(token).unregisterCallback(mediaChanged);
        updatePlayback(true);
        updateVolume(true);
        publish();
        publishProgress(false);
    }

    private void refreshEndpoint() {
        String address = prefs.getString(SetupActivity.PC_ADDRESS, "");
        BluetoothAdapter adapter = getSystemService(android.bluetooth.BluetoothManager.class).getAdapter();
        boolean enabled = listenerReady && !address.isEmpty() && SetupActivity.hasBluetoothPermission(this)
            && adapter != null && adapter.isEnabled();
        if (enabled && endpoint != null && address.equals(allowedAddress)) return;
        if (endpoint != null) endpoint.close();
        stopWatchingMedia();
        endpoint = null;
        allowedAddress = "";
        if (enabled) {
            allowedAddress = address;
            endpoint = new GattEndpoint(this, adapter, address);
            if (!endpoint.open()) {
                endpoint = null;
                setStatus("蓝牙服务启动失败");
            }
        } else {
            setStatus(listenerReady ? "等待选择电脑并开启蓝牙" : "等待系统连接媒体服务");
        }
    }

    private static boolean playing(MediaController controller) {
        PlaybackState playback = controller.getPlaybackState();
        return playback != null && playback.getState() == PlaybackState.STATE_PLAYING;
    }

    private void updatePlayback(boolean checkTitle) {
        if (!listenerReady) return;
        List<MediaController> candidates = watchingMedia ? sessions : activeSessions();
        MediaController next = candidates.isEmpty() ? null : candidates.get(0);
        boolean nextPlaying = false;
        for (MediaController controller : candidates) {
            if (playing(controller)) {
                next = controller;
                nextPlaying = true;
                break;
            }
        }
        boolean sessionChanged = !sameSession(selected, next);
        if (checkTitle || sessionChanged) {
            MediaMetadata metadata = next == null ? null : next.getMetadata();
            String nextTitle = limitTitle(metadata == null ? null : metadata.getDescription().getTitle());
            String nextId = metadata == null ? null : metadata.getString(MediaMetadata.METADATA_KEY_MEDIA_ID);
            int nextDuration = metadata == null ? 0 : (int) Math.min(Integer.MAX_VALUE,
                Math.max(0, metadata.getLong(MediaMetadata.METADATA_KEY_DURATION)));
            if (sessionChanged || !nextTitle.equals(title) || !Objects.equals(nextId, mediaId) || nextDuration != duration) {
                title = nextTitle;
                mediaId = nextId;
                duration = nextDuration;
                trackRevision = nextRevision(trackRevision);
                changed = true;
            }
        }
        selected = next;
        boolean nextHasSession = next != null;
        changed |= hasSession != nextHasSession || isPlaying != nextPlaying;
        hasSession = nextHasSession;
        isPlaying = nextPlaying;
    }

    private static boolean sameSession(MediaController first, MediaController second) {
        return first == second || (first != null && second != null && first.getSessionToken().equals(second.getSessionToken()));
    }

    private void updateVolume(boolean checkBounds) {
        if (!listenerReady) return;
        int next = audio.getStreamVolume(AudioManager.STREAM_MUSIC);
        if (checkBounds || volume < 0) {
            int newMin = audio.getStreamMinVolume(AudioManager.STREAM_MUSIC);
            int newMax = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
            changed |= minimum != newMin || maximum != newMax;
            minimum = newMin;
            maximum = newMax;
        }
        changed |= volume != next;
        volume = next;
    }

    private void publishPlayback(boolean checkTitle) {
        updatePlayback(checkTitle);
        publish();
        publishProgress(false);
    }

    private boolean publishVolume(boolean checkBounds) {
        updateVolume(checkBounds);
        return publish();
    }

    private boolean publish() {
        if (!changed || volume < 0) return false;
        changed = false;
        revision = nextRevision(revision);
        if (endpoint != null) endpoint.changed();
        return true;
    }

    private static int nextRevision(int value) { return value == -1 ? 1 : value + 1; }

    int revision() { return revision; }

    byte[] summary() {
        return ByteBuffer.allocate(13).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(revision).putInt(trackRevision).put((byte) 0xD1)
            .put((byte) ((hasSession ? 1 : 0) | (isPlaying ? 2 : 0)))
            .put((byte) minimum).put((byte) maximum).put((byte) volume).array();
    }

    byte[] readSummary() {
        updatePlayback(false);
        updateVolume(true);
        publish();
        return summary();
    }

    byte[] readProgress() {
        updatePlayback(false);
        publish();
        return progressSnapshot();
    }

    private byte[] progressSnapshot() {
        PlaybackState state = selected == null ? null : selected.getPlaybackState();
        long position = state == null ? -1 : state.getPosition();
        float speed = state != null && state.getState() == PlaybackState.STATE_PLAYING ? state.getPlaybackSpeed() : 0;
        if (!Float.isFinite(speed)) speed = 0;
        if (position >= 0 && state.getLastPositionUpdateTime() > 0) {
            position += (long) (speed * Math.max(0, SystemClock.elapsedRealtime() - state.getLastPositionUpdateTime()));
            position = Math.max(0, Math.min(position, duration > 0 ? duration : Integer.MAX_VALUE));
        }
        return ByteBuffer.allocate(17).order(ByteOrder.LITTLE_ENDIAN).putInt(trackRevision)
            .putInt((int) Math.min(position, Integer.MAX_VALUE)).putInt(duration).putFloat(speed)
            .put((byte) (canSeek(state) ? 1 : 0)).array();
    }

    void publishProgress(boolean force) {
        if (endpoint == null || !endpoint.wantsProgress()) return;
        byte[] snapshot = progressSnapshot();
        long now = SystemClock.elapsedRealtime();
        if (!force && lastProgress != null) {
            ByteBuffer previous = ByteBuffer.wrap(lastProgress).order(ByteOrder.LITTLE_ENDIAN);
            ByteBuffer next = ByteBuffer.wrap(snapshot).order(ByteOrder.LITTLE_ENDIAN);
            int previousPosition = previous.getInt(4), position = next.getInt(4);
            float speed = previous.getFloat(12);
            int length = next.getInt(8);
            double expected = previousPosition < 0 ? -1 : Math.max(0,
                Math.min(length > 0 ? length : Integer.MAX_VALUE, previousPosition + speed * (now - progressAt)));
            if (previous.getInt(0) == next.getInt(0) && previous.getInt(8) == length &&
                speed == next.getFloat(12) && previous.get(16) == next.get(16) &&
                (previousPosition < 0) == (position < 0) && Math.abs(expected - position) < 850) return;
        }
        lastProgress = snapshot;
        progressAt = now;
        endpoint.progressChanged(snapshot);
    }

    private boolean canSeek(PlaybackState state) {
        return duration > 0 && state != null && (state.getActions() & PlaybackState.ACTION_SEEK_TO) != 0;
    }

    boolean seek(int expectedTrack, int position) {
        updatePlayback(true);
        publish();
        if (selected == null || expectedTrack != trackRevision || position < 0 || position > duration ||
            !canSeek(selected.getPlaybackState())) return false;
        selected.getTransportControls().seekTo(position);
        return true;
    }

    byte[] readState() {
        updatePlayback(true);
        updateVolume(true);
        publish();
        try {
            JSONObject payload = new JSONObject();
            payload.put("v", 1);
            payload.put("title", title);
            payload.put("session", hasSession);
            payload.put("playing", isPlaying);
            payload.put("volume", volume);
            payload.put("min", minimum);
            payload.put("max", maximum);
            payload.put("revision", Integer.toUnsignedLong(revision));
            payload.put("trackRevision", Integer.toUnsignedLong(trackRevision));
            return payload.toString().getBytes(StandardCharsets.UTF_8);
        } catch (JSONException error) {
            Log.w(TAG, "Could not encode media state", error);
            return new byte[0];
        }
    }

    private static String limitTitle(CharSequence title) {
        if (title == null) return "";
        StringBuilder result = new StringBuilder();
        title.toString().codePoints().filter(code -> !Character.isISOControl(code)).limit(72)
            .forEach(result::appendCodePoint);
        return result.toString();
    }

    boolean command(int operation) {
        if (operation == 4 || operation == 5) {
            audio.adjustStreamVolume(AudioManager.STREAM_MUSIC,
                operation == 4 ? AudioManager.ADJUST_LOWER : AudioManager.ADJUST_RAISE, 0);
            if (watchingMedia) {
                handler.removeCallbacks(readAfterVolume);
                handler.removeCallbacks(volumeFallback);
                handler.post(readAfterVolume);
                handler.postDelayed(volumeFallback, 120);
            }
            return true;
        }
        updatePlayback(false);
        publish();
        if (selected == null) return false;
        MediaController.TransportControls control = selected.getTransportControls();
        switch (operation) {
            case 1 -> { if (isPlaying) control.pause(); else control.play(); }
            case 2 -> control.skipToPrevious();
            case 3 -> control.skipToNext();
            default -> { return false; }
        }
        if (watchingMedia) {
            handler.removeCallbacks(playbackFallback);
            handler.postDelayed(playbackFallback, 250);
        }
        return true;
    }

    private final Runnable playbackFallback = () -> publishPlayback(true);
    private final Runnable volumeFallback = () -> publishVolume(false);
    private final Runnable readAfterVolume = () -> {
        if (publishVolume(false)) handler.removeCallbacks(volumeFallback);
    };

    void onPeerDisconnected() { stopWatchingMedia(); }

    void onSubscriptionChanged(boolean enabled) {
        if (enabled) startWatchingMedia();
        else stopWatchingMedia();
    }

    static String status() { return serviceStatus; }

    void setStatus(String value) {
        if (!value.equals(serviceStatus)) Log.i(TAG, value);
        serviceStatus = value;
    }
}
