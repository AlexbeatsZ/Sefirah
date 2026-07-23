using CommunityToolkit.WinUI;
using Sefirah.Data.AppDatabase.Repository;
using Sefirah.Data.Models;

namespace Sefirah.Services;
public class MessageHandler(
    RemoteAppRepository remoteAppRepository,
    CallLogRepository callLogRepository,
    ContactRepository contactRepository,
    IDeviceManager deviceManager,
    INotificationFeature notificationFeature,
    IBatteryAlertFeature batteryAlertFeature,
    IClipboardFeature clipboardFeature,
    ISmsFeature smsFeature,
    IFileTransferService fileTransferService,
    IMediaFeature mediaFeature,
    IRemoteMediaFeature remoteMediaFeature,
    IActionFeature actionFeature,
    ISftpFeature sftpFeature,
    ISessionManager sessionManager,
    ICallFeature callFeature,
    IBluetoothPairingService bluetoothPairingService,
    ILocalBluetoothController localBluetoothController,
    IHeadsetHandoffService headsetHandoffService,
    ILogger<MessageHandler> logger) : IMessageHandler
{
    public async void HandleMessageAsync(PairedDevice device, SocketMessage message)
    {
        try
        {
            switch (message)
            {
                case ApplicationList applicationList:
                    await remoteAppRepository.UpdateApplicationList(device, applicationList);
                    break;

                case ApplicationInfo applicationInfo:
                    await remoteAppRepository.AddOrUpdateApplicationForDevice(applicationInfo, device.Id);
                    break;

                case NotificationInfo notificationMessage:
                    await notificationFeature.HandleNotificationMessage(device, notificationMessage);
                    break;

                case MediaAction action:
                    await mediaFeature.HandleMediaActionAsync(action);
                    break;

                case PlaybackInfo playbackSession:
                    await remoteMediaFeature.HandleRemotePlaybackSessionAsync(device, playbackSession);
                    break;

                case BatteryState batteryStatus:
                    await batteryAlertFeature.HandleBatteryStateAsync(device, batteryStatus);
                    break;

                case RingerModeState ringerMode:
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.RingerMode = ringerMode.Mode);
                    break;

                case DndState dndStatus:
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.DndEnabled = dndStatus.IsEnabled);
                    break;

                case AudioStreamState audioStream:
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                        device.UpdateStreamLevel(audioStream.StreamType, audioStream.Level));
                    break;

                case ClipboardInfo clipboard:
                    await clipboardFeature.SetContentAsync(clipboard, device);
                    break;

                case ConversationInfo textConversation:
                    await smsFeature.HandleTextMessage(device.Id, textConversation);
                    break;

                case ContactInfo contactMessage:
                    await contactRepository.SaveContactAsync(device.Id, contactMessage);
                    break;

                case ActionInfo action:
                    if (RemoteActionPolicy.CanExecute(device.Capabilities))
                    {
                        actionFeature.HandleActionMessage(action);
                    }
                    else
                    {
                        logger.Warn($"Ignored remote action from device without controller capability: {device.Name}");
                    }
                    break;

                case SftpServerInfo sftpServerInfo:
                    await sftpFeature.InitializeAsync(device, sftpServerInfo);
                    break;

                case FileTransferInfo fileTransfer:
                    await fileTransferService.ReceiveFiles(fileTransfer, device);
                    break;

                case DeviceInfo deviceInfo:
                    await deviceManager.UpdateDeviceInfo(device, deviceInfo);
                    if (device.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
                        await headsetHandoffService.SendConfigurationAsync(device);
                    break;

                case CallInfo callInfo:
                    await callFeature.HandleCallInfoAsync(device, callInfo);
                    break;

                case CallLogInfo callLogInfo:
                    await callLogRepository.SaveCallLogAsync(device.Id, callLogInfo);
                    break;

                case BluetoothPairingResult pairingResult:
                    bluetoothPairingService.HandleBluetoothPairingResult(device, pairingResult);
                    break;

                case BluetoothDeviceCatalogRequest catalogRequest:
                    if (!device.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
                    {
                        logger.Warn($"Ignored Bluetooth catalog request from unsupported device: {device.Name}");
                        break;
                    }
                    device.SendMessage(await localBluetoothController.GetCatalogAsync(catalogRequest.RequestId));
                    break;

                case BluetoothDeviceCatalog catalog:
                    headsetHandoffService.HandleCatalog(device, catalog);
                    break;

                case BluetoothHandoffCommand command:
                    if (!device.SupportsCapability(ProtocolCapabilities.BluetoothHandoffV1))
                    {
                        logger.Warn($"Ignored Bluetooth command from unsupported device: {device.Name}");
                        break;
                    }
                    device.SendMessage(await localBluetoothController.ExecuteAsync(command));
                    break;

                case BluetoothHandoffResult handoffResult:
                    headsetHandoffService.HandleResult(device, handoffResult);
                    break;

                case BluetoothHandoffRequest handoffRequest:
                    headsetHandoffService.HandleRequest(device, handoffRequest);
                    break;

                case BluetoothDisconnectRequest disconnectRequest:
                    headsetHandoffService.HandleDisconnectRequest(device, disconnectRequest);
                    break;

                case BluetoothHandoffRefreshRequest refreshRequest:
                    headsetHandoffService.HandleRefreshRequest(device, refreshRequest);
                    break;

                case BluetoothHeadsetVisibilityRequest visibilityRequest:
                    headsetHandoffService.HandleVisibilityRequest(device, visibilityRequest);
                    break;

                case BluetoothHandoffConfiguration:
                case BluetoothHandoffState:
                    // Desktop peers own their local configuration and state. These messages
                    // are companion-facing, but are valid between capability-compatible peers.
                    logger.Debug($"Ignored companion Bluetooth state from desktop peer: {device.Name}");
                    break;

                case Disconnect:
                    sessionManager.DisconnectDevice(device, true);
                    break;

                default:
                    logger.Warn($"Unknown message type received: {message.GetType().Name}");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.Error("Error handling message", ex);
        }
    }
}
