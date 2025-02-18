<br />
<div align="center">

  <h2 align="center">ADS FoE Access</h2>

  <p align="center">
    Perform firmware update via FoE using TwinCAT.ADS for .NET
  </p>
</div>

#### How to use
```csharp
string ecmasterNetId = "5.76.203.150.2.1";
uint ecSlaveAddress = 1002;
string efwFilePath = @"C:\some\path\EL2014-0000-SW03.efw";

await FoEAccess.DownloadFirmware(ecmasterNetId, ecSlaveAddress, efwFilePath);
```