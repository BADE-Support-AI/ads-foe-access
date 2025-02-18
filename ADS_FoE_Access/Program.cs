using ADS_FoE_Access;
using System.Security.Cryptography;
using System.Threading;
using TwinCAT.Ads;
using System.Text;
using System.Collections;
using System;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace ADS_FoE_Access;

internal class Program
{
    static async Task Main()
	{
        string ecmasterNetId = "5.76.203.150.2.1";
        uint ecSlaveAddress = 1002;
        string efwFilePath1 = @"C:\Users\Mathis Junker\Desktop\EL2014-0000-SW03.efw";
        string efwFilePath2 = @"C:\Users\Mathis Junker\Desktop\EL2014-0000-SW04.efw";

        await FoEAccess.DownloadFirmware(ecmasterNetId, ecSlaveAddress, efwFilePath2);

	}
}


public static class FoEAccess
{
    private enum DeviceState : byte
    {
        init = 1,
        preop,
        bootstrap,
        safeop,
        op = 8,
    }

    private enum AdsPorts : int
    {
        systemService = 10_000,
        ecMaster = 0xFFFF,
    }

    private enum AdsIdxGroups : uint
    {
        sysServFOpen = 120,
        sysServFClose = 121,
        sysServFRead = 122,
        sysServFFind = 133,
        ecStateMachine = 9,
        ecFoeWriteHdl = 0xF402, 
        ecFoeReadHdl = 0xF401,
        ecFoeWrite = 0xF405,
        ecFoeClose = 0xF403,
    }

    private enum AdsIdxOffsets : uint
    {
        sysServFOpenRead = 1,
        sysServFOpenWrite = 2,   
        sysServFOpenBinary = 16,
    }

    private struct AdsFileInfo
    {
        public DateTime creationTime;
        public DateTime lastAccessTime;
        public DateTime lastWriteTime;
        public long fileSize;
        public string fileName;
        public bool isReadOnly;
        public bool isHidden;
        public bool isSystemFile;
        public bool isDirectory;
        public bool isEncrypted;
    }

    private static async Task<byte> GetSlaveState(
        string netId,
		uint slaveAddress,
        CancellationToken cancel = default)
    {
		using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)AdsPorts.ecMaster);

		byte[] stateBuffer = new byte[2];

        var rRes = await adsClient.ReadAsync(
            (uint)AdsIdxGroups.ecStateMachine,
            slaveAddress,
            stateBuffer,
            cancel);

        rRes.ThrowOnError();

        byte deviceState = stateBuffer[0];
        byte linkState = stateBuffer[1];

        return deviceState;
    }

	private static async Task RequestSlaveState(
		string netId,
		uint slaveAddress,
		byte deviceStateReq,
		CancellationToken cancel = default)
	{
		using AdsClient adsClient = new();
		adsClient.Connect(netId, (int)AdsPorts.ecMaster);

		var wRes = await adsClient.WriteAsync(
            (uint)AdsIdxGroups.ecStateMachine,
			slaveAddress,
			new byte[]{deviceStateReq, 0},
			cancel);

        wRes.ThrowOnError();
}

	private static async Task SetSlaveState(
        string netId,
        uint slaveAddress,
        byte deviceStateReq,
        CancellationToken cancel = default)
    {
		await RequestSlaveState(
			netId,
			slaveAddress,
			deviceStateReq,
			cancel);

        for (int tries = 0; tries < 5; tries++)
        {
            var slaveStateRead = await GetSlaveState(
                netId,
                slaveAddress,
                cancel);

            if (deviceStateReq == slaveStateRead)
            {
                return;
            }
            Thread.Sleep(500);
        }

        throw new InvalidOperationException(); // Status change failed
	}

	private static async Task<uint> GetFoEHandle(
        string netId,
        uint slaveAddress,
		string fileName,
		bool modeWrite = true,
		uint password = 0,
        CancellationToken cancel = default)
	{
		using AdsClient adsClient = new();

		adsClient.Connect(netId, (int)slaveAddress);

        byte[] readBfr = new byte[255];

        var rwRes = await adsClient.ReadWriteAsync(
            (uint)(modeWrite ? AdsIdxGroups.ecFoeWriteHdl : AdsIdxGroups.ecFoeReadHdl), 
			password, 
			readBfr, 
			Encoding.UTF8.GetBytes(fileName),
			cancel);

        rwRes.ThrowOnError();

        int zeroIndex = Array.IndexOf(readBfr, (byte)0);
		if (zeroIndex < 1 || zeroIndex > 4)
		{
			throw new Exception();	// Handle has to be a 32 bit uint
		}

        return BitConverter.ToUInt32(readBfr, 0);
    }

    private static async Task<uint> FileOpenReadingAsync(
		string netId,
        string path,
        bool binaryOpen = true,
        CancellationToken cancel = default)
    {
        uint tmpOpenMode = (uint)AdsIdxOffsets.sysServFOpenRead | ((uint)1<<16);
        if (binaryOpen) tmpOpenMode |= (uint)AdsIdxOffsets.sysServFOpenBinary;

        return await FileOpenAsync(netId, path, tmpOpenMode, cancel);
    }

    private static async Task<uint> FileOpenAsync(
		string netId,
		string path,
		uint openFlags,
		CancellationToken cancel = default)
    {
        byte[] pathBuffer = Encoding.ASCII.GetBytes(path + '\0');

        byte[] handleBuffer = new byte[sizeof(UInt32)];

		using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)AdsPorts.systemService);

        var rwResult = await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.sysServFOpen,
            openFlags,
            handleBuffer,
            pathBuffer,
            cancel);

        rwResult.ThrowOnError();

        return BitConverter.ToUInt32(handleBuffer);
    }


    private static async Task<AdsFileInfo> GetFileInfoAsync(
		string netId,
        string filePath,
        CancellationToken cancel = default)
    {
        uint hFile = await FileOpenReadingAsync(netId, filePath, false, cancel);

        byte[] pathBuffer = Encoding.UTF8.GetBytes(filePath);
        byte[] fileInfoBuffer = new byte[324];

        using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)AdsPorts.systemService);

        var rwRes = await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.sysServFFind,
            hFile,
            fileInfoBuffer,
            pathBuffer,
            cancel);

        rwRes.ThrowOnError();

        await FileCloseAsync(netId, hFile, cancel);

        AdsFileInfo info = new();

        info.creationTime = DateTime.FromFileTime(BitConverter.ToInt64(fileInfoBuffer, 8));
        info.lastAccessTime = DateTime.FromFileTime(BitConverter.ToInt64(fileInfoBuffer, 16));
        info.lastWriteTime = DateTime.FromFileTime(BitConverter.ToInt64(fileInfoBuffer, 24));
		uint fileSizeHigh = BitConverter.ToUInt32(fileInfoBuffer, 32);
		uint fileSizeLow = BitConverter.ToUInt32(fileInfoBuffer, 36);
		info.fileSize = (long)fileSizeHigh << 32 | fileSizeLow;
		
        int fileNameLength = Array.IndexOf(fileInfoBuffer, (byte)0, 48, 260) - 48;
        int altFileNameLength = Array.IndexOf(fileInfoBuffer, (byte)0, 304, 16) - 304;

        info.fileName = Encoding.ASCII.GetString(fileInfoBuffer, 48, fileNameLength >= 0 ? fileNameLength : 260);

        info.isReadOnly = (fileInfoBuffer[4] & (1 << 0)) != 0;
        info.isHidden = (fileInfoBuffer[4] & (1 << 1)) != 0;
        info.isSystemFile = (fileInfoBuffer[4] & (1 << 2)) != 0;
        info.isDirectory = (fileInfoBuffer[4] & (1 << 4)) != 0;
        info.isEncrypted = (fileInfoBuffer[5] & (1 << 6)) != 0;

        return info;
    }


    private static async Task<byte[]> FileReadChunkAsync(
		string netId,
        uint hFile,
        uint chunkSize,
        CancellationToken cancel = default)
    {
        byte[] rdBfr = new byte[chunkSize];

        using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)AdsPorts.systemService);

        var readWriteResult = await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.sysServFRead,
            hFile,
            rdBfr,
            new byte[4],
            cancel);

        readWriteResult.ThrowOnError();

        if (readWriteResult.ReadBytes < chunkSize)
        {
            return rdBfr.Take(readWriteResult.ReadBytes).ToArray();
        }

        return rdBfr.ToArray();
    }

    private static async Task FoEWriteChunkAsync(
		string netId,
		uint slaveAddress,
        uint hFoE,
        byte[] chunk,
        CancellationToken cancel = default)
    {
        using AdsClient adsClient = new();
		adsClient.Timeout = 50_000;

        adsClient.Connect(netId, (int)slaveAddress);

		byte[] readBuffer = new byte[255];

        var rwRes = await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.ecFoeWrite,
            hFoE,
            readBuffer,
            chunk,
            cancel);

        rwRes.ThrowOnError();
    }

    private static async Task FileCloseAsync(string netId, uint hFile, CancellationToken cancel = default)
    {
        using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)AdsPorts.systemService);

        await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.sysServFClose,
            hFile,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            cancel);
    }

    private static async Task FoECloseAsync(
        string netId, 
        uint slaveAddress,
        uint hFoE, 
        CancellationToken cancel = default)
    {
        using AdsClient adsClient = new();

        adsClient.Connect(netId, (int)slaveAddress);

		byte[] readBuffer = new byte[255];

        var rwResult = await adsClient.ReadWriteAsync(
            (uint)AdsIdxGroups.ecFoeClose,
			hFoE,
            readBuffer,
            Array.Empty<byte>(),
            cancel);

        rwResult.ThrowOnError();
    }



    public static async Task DownloadFirmware(
		string ecMasterNetId,
		uint ecSlaveAddress,
		string efwFilePath,
        CancellationToken cancel = default)
    {
		string netIdLocal = AmsNetId.Local.ToString();

		uint hFileUpload = await FileOpenReadingAsync(
			netIdLocal, 
			efwFilePath, 
			true,
			cancel);

        if (hFileUpload == 0)
        {
            return;
		}

        var slaveStateOld = await GetSlaveState(
            ecMasterNetId,
            ecSlaveAddress,
            cancel);

		// Slave needs to be in bootstrap state for firmware upload
		if (slaveStateOld != (byte)DeviceState.bootstrap)
		{
			if (slaveStateOld != (byte)DeviceState.init)
			{
				await SetSlaveState(
                    ecMasterNetId,
                    ecSlaveAddress, 
					(byte)DeviceState.init, 
					cancel);
			}

            await SetSlaveState(
                ecMasterNetId,
                ecSlaveAddress, 
			    (byte)DeviceState.bootstrap,
			    cancel);
        }

		uint hFileFoE = await GetFoEHandle(
            ecMasterNetId,
            ecSlaveAddress,
			Path.GetFileName(efwFilePath),
			true,
			0,
			cancel);

        AdsFileInfo efwFileInfo = await GetFileInfoAsync(netIdLocal, efwFilePath, cancel);
		long chunkSizeBytes = efwFileInfo.fileSize;

        cancel.ThrowIfCancellationRequested();

		// This reads the full file at once and transfers it to the target terminal. 
		// For larger file types than efw this should be done chunk-wise
        byte[] fileContentBuffer = await FileReadChunkAsync(
            netIdLocal,
            hFileUpload,
            (uint)chunkSizeBytes,
            cancel);

        await FoEWriteChunkAsync(
			ecMasterNetId,
            ecSlaveAddress,
            hFileFoE,
            fileContentBuffer,
			cancel);

        await FileCloseAsync(netIdLocal, hFileUpload, cancel);
        await FoECloseAsync(ecMasterNetId, ecSlaveAddress, hFileFoE, cancel);

        // Bootstrap -> init -> original state before firmware update
        await SetSlaveState(ecMasterNetId, ecSlaveAddress, (byte)DeviceState.init, cancel);
        await SetSlaveState(ecMasterNetId, ecSlaveAddress, slaveStateOld, cancel);
    }

}