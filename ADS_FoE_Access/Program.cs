using System.Text;
using TwinCAT.Ads;

namespace ADS_FoE_Access;


internal class Program
{
    private static async Task Main()
    {
        const string etherCatMasterNetId = "1.2.3.4.2.1";
        const uint etherCatSlaveAddress = 1002;
        const string firmwareFilePath = @"C:\Path\To\Your\File.efw";

        await FoEAccess.DownloadFirmwareAsync(
            etherCatMasterNetId,
            etherCatSlaveAddress,
            firmwareFilePath);
    }
}

public static class FoEAccess
{
    private const int StateChangeRetries = 5;
    private static readonly TimeSpan StateChangeRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FoETransferTimeout = TimeSpan.FromSeconds(50);

    private enum DeviceState : byte
    {
        Init = 1,
        PreOp = 2,
        Bootstrap = 3,
        SafeOp = 4,
        Op = 8
    }

    private enum AdsPort
    {
        SystemService = 10_000,
        EtherCatMaster = 0xFFFF
    }

    private enum AdsIndexGroup : uint
    {
        SystemServiceFileOpen = 120,
        SystemServiceFileClose = 121,
        SystemServiceFileRead = 122,
        SystemServiceFileFind = 133,

        EtherCatStateMachine = 9,
        EtherCatFoEReadHandle = 0xF401,
        EtherCatFoEWriteHandle = 0xF402,
        EtherCatFoEClose = 0xF403,
        EtherCatFoEWrite = 0xF405
    }

    [Flags]
    private enum FileOpenMode : uint
    {
        Read = 1,
        Write = 2,
        Binary = 16,
        Shared = 1u << 16
    }

    private readonly record struct AdsFileInfo(long FileSize);

    /// <summary>
    /// Transfers an EFW firmware file to an EtherCAT slave via FoE.
    /// The original EtherCAT state is restored after the transfer or
    /// if the transfer fails.
    /// </summary>
    public static async Task DownloadFirmwareAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        string firmwareFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etherCatMasterNetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareFilePath);

        string localNetId = AmsNetId.Local.ToString();
        uint localFileHandle = 0;
        uint foeHandle = 0;
        DeviceState? originalState = null;

        try
        {
            localFileHandle = await OpenFileForReadingAsync(
                localNetId,
                firmwareFilePath,
                cancellationToken);

            AdsFileInfo firmwareFileInfo = await GetFileInfoAsync(
                localNetId,
                firmwareFilePath,
                cancellationToken);

            if (firmwareFileInfo.FileSize <= 0)
            {
                throw new InvalidOperationException(
                    $"The firmware file '{firmwareFilePath}' is empty.");
            }

            if (firmwareFileInfo.FileSize > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "The firmware file is too large for the current transfer implementation. " +
                    "The file must be transferred in multiple chunks.");
            }

            originalState = await GetSlaveStateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                cancellationToken);

            await SetSlaveStateForFirmwareUpdateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                originalState.Value,
                cancellationToken);

            foeHandle = await OpenFoEWriteHandleAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                Path.GetFileName(firmwareFilePath),
                password: 0,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // The current implementation transfers the complete EFW file in a single chunk.
            byte[] firmwareContent = await ReadFileChunkAsync(
                localNetId,
                localFileHandle,
                checked((uint)firmwareFileInfo.FileSize),
                cancellationToken);

            await WriteFoEChunkAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                foeHandle,
                firmwareContent,
                cancellationToken);
        }
        finally
        {
            // Release resources whenever possible, even if the transfer has failed.
            // CancellationToken.None ensures that an already cancelled operation
            // does not prevent handles from being closed.
            if (foeHandle != 0)
            {
                await CloseFoEAsync(
                    etherCatMasterNetId,
                    etherCatSlaveAddress,
                    foeHandle,
                    CancellationToken.None);
            }

            if (localFileHandle != 0)
            {
                await CloseFileAsync(
                    localNetId,
                    localFileHandle,
                    CancellationToken.None);
            }

            if (originalState.HasValue)
            {
                await RestoreSlaveStateAsync(
                    etherCatMasterNetId,
                    etherCatSlaveAddress,
                    originalState.Value,
                    CancellationToken.None);
            }
        }
    }

    private static AdsClient CreateAdsClient(string netId, int port, TimeSpan? timeout = null)
    {
        var adsClient = new AdsClient();

        if (timeout.HasValue)
        {
            adsClient.Timeout = checked((int)timeout.Value.TotalMilliseconds);
        }

        adsClient.Connect(netId, port);
        return adsClient;
    }

    private static async Task<DeviceState> GetSlaveStateAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            etherCatMasterNetId,
            (int)AdsPort.EtherCatMaster);

        byte[] stateBuffer = new byte[2];

        var result = await adsClient.ReadAsync(
            (uint)AdsIndexGroup.EtherCatStateMachine,
            etherCatSlaveAddress,
            stateBuffer,
            cancellationToken);

        result.ThrowOnError();

        return (DeviceState)stateBuffer[0];
    }

    private static async Task RequestSlaveStateAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        DeviceState requestedState,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            etherCatMasterNetId,
            (int)AdsPort.EtherCatMaster);

        var result = await adsClient.WriteAsync(
            (uint)AdsIndexGroup.EtherCatStateMachine,
            etherCatSlaveAddress,
            new[] { (byte)requestedState, (byte)0 },
            cancellationToken);

        result.ThrowOnError();
    }

    private static async Task SetSlaveStateAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        DeviceState requestedState,
        CancellationToken cancellationToken)
    {
        await RequestSlaveStateAsync(
            etherCatMasterNetId,
            etherCatSlaveAddress,
            requestedState,
            cancellationToken);

        for (int attempt = 1; attempt <= StateChangeRetries; attempt++)
        {
            DeviceState currentState = await GetSlaveStateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                cancellationToken);

            if (currentState == requestedState)
            {
                return;
            }

            if (attempt < StateChangeRetries)
            {
                await Task.Delay(StateChangeRetryDelay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"EtherCAT slave {etherCatSlaveAddress} could not transition to state " +
            $"'{requestedState}'.");
    }

    private static async Task SetSlaveStateForFirmwareUpdateAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        DeviceState currentState,
        CancellationToken cancellationToken)
    {
        if (currentState == DeviceState.Bootstrap)
        {
            return;
        }

        if (currentState != DeviceState.Init)
        {
            await SetSlaveStateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                DeviceState.Init,
                cancellationToken);
        }

        await SetSlaveStateAsync(
            etherCatMasterNetId,
            etherCatSlaveAddress,
            DeviceState.Bootstrap,
            cancellationToken);
    }

    private static async Task RestoreSlaveStateAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        DeviceState originalState,
        CancellationToken cancellationToken)
    {
        DeviceState currentState = await GetSlaveStateAsync(
            etherCatMasterNetId,
            etherCatSlaveAddress,
            cancellationToken);

        if (currentState != DeviceState.Init)
        {
            await SetSlaveStateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                DeviceState.Init,
                cancellationToken);
        }

        if (originalState != DeviceState.Init)
        {
            await SetSlaveStateAsync(
                etherCatMasterNetId,
                etherCatSlaveAddress,
                originalState,
                cancellationToken);
        }
    }

    private static async Task<uint> OpenFoEWriteHandleAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        string fileName,
        uint password,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            etherCatMasterNetId,
            checked((int)etherCatSlaveAddress));

        byte[] handleBuffer = new byte[sizeof(uint)];

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.EtherCatFoEWriteHandle,
            password,
            handleBuffer,
            Encoding.UTF8.GetBytes(fileName),
            cancellationToken);

        result.ThrowOnError();

        if (result.ReadBytes < sizeof(uint))
        {
            throw new InvalidOperationException(
                "The EtherCAT master did not return a valid FoE handle.");
        }

        uint foeHandle = BitConverter.ToUInt32(handleBuffer, 0);

        if (foeHandle == 0)
        {
            throw new InvalidOperationException(
                "The EtherCAT master returned an invalid FoE handle.");
        }

        return foeHandle;
    }

    private static async Task<uint> OpenFileForReadingAsync(
        string localNetId,
        string filePath,
        CancellationToken cancellationToken)
    {
        byte[] filePathBuffer = Encoding.ASCII.GetBytes(filePath + '\0');
        byte[] handleBuffer = new byte[sizeof(uint)];

        using AdsClient adsClient = CreateAdsClient(
            localNetId,
            (int)AdsPort.SystemService);

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.SystemServiceFileOpen,
            (uint)(FileOpenMode.Read | FileOpenMode.Binary | FileOpenMode.Shared),
            handleBuffer,
            filePathBuffer,
            cancellationToken);

        result.ThrowOnError();

        uint fileHandle = BitConverter.ToUInt32(handleBuffer, 0);

        if (fileHandle == 0)
        {
            throw new InvalidOperationException(
                $"The file '{filePath}' could not be opened.");
        }

        return fileHandle;
    }

    private static async Task<AdsFileInfo> GetFileInfoAsync(
        string localNetId,
        string filePath,
        CancellationToken cancellationToken)
    {
        uint fileHandle = await OpenFileForReadingAsync(
            localNetId,
            filePath,
            cancellationToken);

        try
        {
            byte[] fileInfoBuffer = new byte[324];

            using AdsClient adsClient = CreateAdsClient(
                localNetId,
                (int)AdsPort.SystemService);

            var result = await adsClient.ReadWriteAsync(
                (uint)AdsIndexGroup.SystemServiceFileFind,
                fileHandle,
                fileInfoBuffer,
                Encoding.UTF8.GetBytes(filePath),
                cancellationToken);

            result.ThrowOnError();

            long fileSize =
                ((long)BitConverter.ToUInt32(fileInfoBuffer, 32) << 32) |
                BitConverter.ToUInt32(fileInfoBuffer, 36);

            return new AdsFileInfo(fileSize);
        }
        finally
        {
            await CloseFileAsync(localNetId, fileHandle, CancellationToken.None);
        }
    }

    private static async Task<byte[]> ReadFileChunkAsync(
        string localNetId,
        uint fileHandle,
        uint chunkSize,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[chunkSize];

        using AdsClient adsClient = CreateAdsClient(
            localNetId,
            (int)AdsPort.SystemService);

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.SystemServiceFileRead,
            fileHandle,
            buffer,
            new byte[sizeof(uint)],
            cancellationToken);

        result.ThrowOnError();

        return result.ReadBytes == buffer.Length
            ? buffer
            : buffer[..result.ReadBytes];
    }

    private static async Task WriteFoEChunkAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        uint foeHandle,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            etherCatMasterNetId,
            checked((int)etherCatSlaveAddress),
            FoETransferTimeout);

        byte[] responseBuffer = new byte[255];

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.EtherCatFoEWrite,
            foeHandle,
            responseBuffer,
            chunk,
            cancellationToken);

        if (result.Failed)
        {
            throw new InvalidOperationException(
                $"FoE firmware transfer failed. ADS error code: {result.ErrorCode}. " +
                "The selected file may not be a compatible firmware file.");
        }
    }

    private static async Task CloseFileAsync(
        string localNetId,
        uint fileHandle,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            localNetId,
            (int)AdsPort.SystemService);

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.SystemServiceFileClose,
            fileHandle,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            cancellationToken);

        result.ThrowOnError();
    }

    private static async Task CloseFoEAsync(
        string etherCatMasterNetId,
        uint etherCatSlaveAddress,
        uint foeHandle,
        CancellationToken cancellationToken)
    {
        using AdsClient adsClient = CreateAdsClient(
            etherCatMasterNetId,
            checked((int)etherCatSlaveAddress));

        byte[] responseBuffer = new byte[255];

        var result = await adsClient.ReadWriteAsync(
            (uint)AdsIndexGroup.EtherCatFoEClose,
            foeHandle,
            responseBuffer,
            Array.Empty<byte>(),
            cancellationToken);

        result.ThrowOnError();
    }
}