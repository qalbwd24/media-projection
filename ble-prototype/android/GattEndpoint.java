package com.weilin.phonemediable;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattServer;
import android.bluetooth.BluetoothGattServerCallback;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.BluetoothStatusCodes;
import android.bluetooth.le.AdvertiseCallback;
import android.bluetooth.le.AdvertiseData;
import android.bluetooth.le.AdvertiseSettings;
import android.bluetooth.le.BluetoothLeAdvertiser;
import android.os.ParcelUuid;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;
import java.util.UUID;

final class GattEndpoint {
    static final UUID SERVICE = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb007");
    static final UUID STATE = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb008");
    static final UUID COMMAND = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb009");
    static final UUID REVISION = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb010");
    static final UUID STATE_PAGE = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb011");
    static final UUID PROGRESS = UUID.fromString("c00fe8e2-30ac-41d2-802d-86309fbbb012");
    static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final int PAGE_BYTES = 20;

    private final MediaListener owner;
    private final BluetoothAdapter adapter;
    private final String allowedAddress;
    private BluetoothGattServer server;
    private BluetoothLeAdvertiser advertiser;
    private boolean advertising;
    private BluetoothDevice peer;
    private boolean subscribed;
    private boolean progressSubscribed;
    private byte[] pendingProgress;
    private boolean notifying;
    private int sentRevision;
    private byte[] cachedRead = {};
    private int currentPage;

    private final BluetoothGattCharacteristic revisionSignal = new BluetoothGattCharacteristic(REVISION,
        BluetoothGattCharacteristic.PROPERTY_NOTIFY | BluetoothGattCharacteristic.PROPERTY_READ,
        BluetoothGattCharacteristic.PERMISSION_READ_ENCRYPTED);
    private final BluetoothGattCharacteristic progressSignal = new BluetoothGattCharacteristic(PROGRESS,
        BluetoothGattCharacteristic.PROPERTY_READ | BluetoothGattCharacteristic.PROPERTY_NOTIFY,
        BluetoothGattCharacteristic.PERMISSION_READ_ENCRYPTED);

    GattEndpoint(MediaListener owner, BluetoothAdapter adapter, String allowedAddress) {
        this.owner = owner;
        this.adapter = adapter;
        this.allowedAddress = allowedAddress;
    }

    boolean open() {
        advertiser = adapter.getBluetoothLeAdvertiser();
        if (advertiser == null) return false;
        server = owner.getSystemService(BluetoothManager.class).openGattServer(owner, callback);
        if (server == null) return false;
        BluetoothGattService service = new BluetoothGattService(SERVICE, BluetoothGattService.SERVICE_TYPE_PRIMARY);
        service.addCharacteristic(new BluetoothGattCharacteristic(STATE, BluetoothGattCharacteristic.PROPERTY_READ,
            BluetoothGattCharacteristic.PERMISSION_READ_ENCRYPTED));
        service.addCharacteristic(new BluetoothGattCharacteristic(COMMAND, BluetoothGattCharacteristic.PROPERTY_WRITE,
            BluetoothGattCharacteristic.PERMISSION_WRITE_ENCRYPTED));
        service.addCharacteristic(new BluetoothGattCharacteristic(STATE_PAGE, BluetoothGattCharacteristic.PROPERTY_WRITE,
            BluetoothGattCharacteristic.PERMISSION_WRITE_ENCRYPTED));
        revisionSignal.addDescriptor(new BluetoothGattDescriptor(CCCD, BluetoothGattDescriptor.PERMISSION_READ_ENCRYPTED
            | BluetoothGattDescriptor.PERMISSION_WRITE_ENCRYPTED));
        service.addCharacteristic(revisionSignal);
        progressSignal.addDescriptor(new BluetoothGattDescriptor(CCCD, BluetoothGattDescriptor.PERMISSION_READ_ENCRYPTED
            | BluetoothGattDescriptor.PERMISSION_WRITE_ENCRYPTED));
        service.addCharacteristic(progressSignal);
        if (server.addService(service)) {
            owner.setStatus("正在启动蓝牙服务");
            return true;
        }
        close();
        return false;
    }

    void close() {
        stopAdvertising();
        if (server != null) {
            server.close();
            server = null;
        }
        clearPeer();
    }

    private void clearPeer() {
        peer = null;
        subscribed = false;
        progressSubscribed = false;
        pendingProgress = null;
        notifying = false;
        sentRevision = 0;
        cachedRead = new byte[0];
        currentPage = 0;
    }

    private void startAdvertising() {
        if (server == null || advertising || peer != null) return;
        AdvertiseSettings settings = new AdvertiseSettings.Builder().setConnectable(true)
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_LOW).build();
        AdvertiseData data = new AdvertiseData.Builder().addServiceUuid(new ParcelUuid(SERVICE)).build();
        advertising = true;
        advertiser.startAdvertising(settings, data, advertisement);
    }

    private void stopAdvertising() {
        if (advertising) {
            advertising = false;
            advertiser.stopAdvertising(advertisement);
        }
    }

    private final AdvertiseCallback advertisement = new AdvertiseCallback() {
        @Override public void onStartSuccess(AdvertiseSettings settings) {
            owner.handler.post(() -> {
                if (server != null && advertising && peer == null) owner.setStatus("等待电脑连接");
            });
        }
        @Override public void onStartFailure(int errorCode) {
            owner.handler.post(() -> {
                if (server == null || !advertising) return;
                advertising = false;
                owner.setStatus("蓝牙广播失败 · " + errorCode);
            });
        }
    };

    private boolean trusted(BluetoothDevice device) {
        return allowedAddress.equalsIgnoreCase(device.getAddress()) && device.getBondState() == BluetoothDevice.BOND_BONDED;
    }

    void changed() {
        notifyRevision();
    }

    boolean wantsProgress() { return progressSubscribed; }

    void progressChanged(byte[] snapshot) {
        if (!progressSubscribed) return;
        pendingProgress = snapshot;
        notifyRevision();
    }

    private void notifyRevision() {
        int revision = owner.revision();
        if (server == null || peer == null || notifying) return;
        boolean sendRevision = subscribed && sentRevision != revision;
        if (!sendRevision && pendingProgress == null) return;
        if (!trusted(peer)) {
            subscribed = false;
            progressSubscribed = false;
            pendingProgress = null;
            return;
        }
        byte[] bytes = sendRevision ? owner.summary() : pendingProgress;
        if (server.notifyCharacteristicChanged(peer, sendRevision ? revisionSignal : progressSignal, false, bytes) == BluetoothStatusCodes.SUCCESS) {
            notifying = true;
            if (sendRevision) sentRevision = revision;
            else pendingProgress = null;
        }
    }

    private void respond(BluetoothDevice device, int request, int code, int offset, byte[] value) {
        server.sendResponse(device, request, code, offset, value);
    }

    private int selectPage(int page) {
        if (page == 0) {
            byte[] payload = owner.readState();
            cachedRead = ByteBuffer.allocate(payload.length + 2).order(ByteOrder.LITTLE_ENDIAN)
                .putShort((short) payload.length).put(payload).array();
        }
        if (page * PAGE_BYTES >= cachedRead.length) return BluetoothGatt.GATT_INVALID_OFFSET;
        currentPage = page;
        return BluetoothGatt.GATT_SUCCESS;
    }

    private final BluetoothGattServerCallback callback = new BluetoothGattServerCallback() {
        @Override public void onServiceAdded(int code, BluetoothGattService service) {
            owner.handler.post(() -> {
                if (server == null) return;
                if (code == BluetoothGatt.GATT_SUCCESS) startAdvertising();
                else owner.setStatus("无法注册蓝牙服务 · " + code);
            });
        }

        @Override public void onConnectionStateChange(BluetoothDevice device, int code, int state) {
            owner.handler.post(() -> {
                if (server == null) return;
                if (state == BluetoothProfile.STATE_CONNECTED && code == BluetoothGatt.GATT_SUCCESS) {
                    if (trusted(device)) {
                        if (!device.equals(peer)) clearPeer();
                        peer = device;
                        stopAdvertising();
                        owner.setStatus("电脑已通过蓝牙连接");
                    }
                } else if (state == BluetoothProfile.STATE_DISCONNECTED && device.equals(peer)) {
                    clearPeer();
                    owner.onPeerDisconnected();
                    startAdvertising();
                }
            });
        }

        @Override public void onCharacteristicReadRequest(BluetoothDevice device, int request, int offset,
                                                          BluetoothGattCharacteristic characteristic) {
            owner.handler.post(() -> {
                if (server == null) return;
                if (!trusted(device)) {
                    respond(device, request, BluetoothGatt.GATT_INSUFFICIENT_AUTHENTICATION, offset, null);
                } else if (REVISION.equals(characteristic.getUuid())) {
                    if (offset == 0) respond(device, request, BluetoothGatt.GATT_SUCCESS, 0, owner.readSummary());
                    else respond(device, request, BluetoothGatt.GATT_INVALID_OFFSET, offset, null);
                } else if (PROGRESS.equals(characteristic.getUuid())) {
                    if (offset == 0) respond(device, request, BluetoothGatt.GATT_SUCCESS, 0, owner.readProgress());
                    else respond(device, request, BluetoothGatt.GATT_INVALID_OFFSET, offset, null);
                } else if (!STATE.equals(characteristic.getUuid())) {
                    respond(device, request, BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED, offset, null);
                } else if (offset != 0 || cachedRead.length == 0) {
                    respond(device, request, BluetoothGatt.GATT_INVALID_OFFSET, offset, null);
                } else {
                    int from = currentPage * PAGE_BYTES;
                    byte[] bytes = Arrays.copyOfRange(cachedRead, from, Math.min(cachedRead.length, from + PAGE_BYTES));
                    respond(device, request, BluetoothGatt.GATT_SUCCESS, offset, bytes);
                }
            });
        }

        @Override public void onCharacteristicWriteRequest(BluetoothDevice device, int request,
                BluetoothGattCharacteristic characteristic, boolean prepared, boolean responseNeeded, int offset, byte[] bytes) {
            owner.handler.post(() -> {
                if (server == null) return;
                int code = BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED;
                if (!trusted(device)) code = BluetoothGatt.GATT_INSUFFICIENT_AUTHENTICATION;
                else if (!prepared && responseNeeded && offset == 0 && bytes.length == 1) {
                    int value = bytes[0] & 255;
                    if (STATE_PAGE.equals(characteristic.getUuid())) code = selectPage(value);
                    if (COMMAND.equals(characteristic.getUuid()) && owner.command(value)) code = BluetoothGatt.GATT_SUCCESS;
                } else if (!prepared && responseNeeded && offset == 0 && bytes.length == 9 && bytes[0] == 6 &&
                    COMMAND.equals(characteristic.getUuid())) {
                    ByteBuffer seek = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
                    if (owner.seek(seek.getInt(1), seek.getInt(5))) code = BluetoothGatt.GATT_SUCCESS;
                }
                if (responseNeeded) respond(device, request, code, offset, null);
            });
        }

        @Override public void onDescriptorReadRequest(BluetoothDevice device, int request, int offset,
                                                      BluetoothGattDescriptor descriptor) {
            owner.handler.post(() -> {
                if (server == null) return;
                if (!trusted(device)) respond(device, request, BluetoothGatt.GATT_INSUFFICIENT_AUTHENTICATION, offset, null);
                else if (offset != 0 || !CCCD.equals(descriptor.getUuid()))
                    respond(device, request, BluetoothGatt.GATT_INVALID_OFFSET, offset, null);
                else respond(device, request, BluetoothGatt.GATT_SUCCESS, offset, (PROGRESS.equals(descriptor.getCharacteristic().getUuid()) ? progressSubscribed : subscribed)
                    ? BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE : BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE);
            });
        }

        @Override public void onDescriptorWriteRequest(BluetoothDevice device, int request, BluetoothGattDescriptor descriptor,
                boolean prepared, boolean responseNeeded, int offset, byte[] bytes) {
            owner.handler.post(() -> {
                if (server == null) return;
                boolean forProgress = PROGRESS.equals(descriptor.getCharacteristic().getUuid());
                int code = BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED;
                if (!trusted(device)) code = BluetoothGatt.GATT_INSUFFICIENT_AUTHENTICATION;
                else if (device.equals(peer) && CCCD.equals(descriptor.getUuid()) && !prepared && offset == 0) {
                    boolean enable = Arrays.equals(bytes, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                    if (enable || Arrays.equals(bytes, BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE)) {
                        if (forProgress) {
                            progressSubscribed = enable;
                            pendingProgress = null;
                        } else {
                            subscribed = enable;
                            sentRevision = 0;
                        }
                        code = BluetoothGatt.GATT_SUCCESS;
                    }
                }
                if (responseNeeded) respond(device, request, code, offset, null);
                if (code == BluetoothGatt.GATT_SUCCESS) {
                    owner.onSubscriptionChanged(subscribed || progressSubscribed);
                    if (forProgress && progressSubscribed) owner.publishProgress(true);
                    notifyRevision();
                }
            });
        }

        @Override public void onNotificationSent(BluetoothDevice device, int code) {
            owner.handler.post(() -> {
                if (device.equals(peer)) {
                    notifying = false;
                    if (code == BluetoothGatt.GATT_SUCCESS) notifyRevision();
                }
            });
        }
    };
}
