using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

const string BundleResource = "SefirahInstaller.Payload.msixbundle";
const string CertificateResource = "SefirahInstaller.SigningCertificate.cer";
const string ExpectedPublisher = "CN=Meta Sefirah Fork";

Console.OutputEncoding = Encoding.UTF8;

if (!IsAdministrator())
{
    Console.Error.WriteLine("安装器需要管理员权限，请接受 Windows UAC 提示后重试。");
    return 1;
}

var temporaryRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Temp", ".agents", "Sefirah", "exe-installer", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

var packagePath = Path.Combine(temporaryRoot, "Sefirah-Fork.msixbundle");
var certificatePath = Path.Combine(temporaryRoot, "Sefirah-Fork-Signing.cer");
var deploymentScriptPath = Path.Combine(temporaryRoot, "Install-Package.ps1");
X509Certificate2? certificate = null;
var certificateAdded = false;

try
{
    ExtractResource(BundleResource, packagePath);
    ExtractResource(CertificateResource, certificatePath);
    certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
    if (!string.Equals(certificate.Subject, ExpectedPublisher, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"安装证书发布者不匹配：{certificate.Subject}");
    }

    using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
    {
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false);
        if (existing.Count == 0)
        {
            store.Add(certificate);
            certificateAdded = true;
            Console.WriteLine($"已信任 Sefirah Fork 安装证书：{certificate.Thumbprint}");
        }
    }

    File.WriteAllText(
        deploymentScriptPath,
        "param([Parameter(Mandatory=$true)][string]$PackagePath)\r\n" +
        "$ErrorActionPreference = 'Stop'\r\n" +
        "Add-AppxPackage -Path $PackagePath\r\n",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    Console.WriteLine("正在安装 Sefirah Fork，请稍候……");
    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-NonInteractive");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(deploymentScriptPath);
    startInfo.ArgumentList.Add("-PackagePath");
    startInfo.ArgumentList.Add(packagePath);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动 Windows 包部署程序。");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Windows 包部署失败，退出代码：{process.ExitCode}");
    }

    Console.WriteLine("Sefirah Fork 安装完成，可从开始菜单启动。");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"安装失败：{exception.Message}");
    if (certificateAdded && certificate is not null)
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }
        catch
        {
            Console.Error.WriteLine("安装失败后未能自动移除安装证书。");
        }
    }
    Console.Error.WriteLine("按 Enter 键退出。");
    Console.ReadLine();
    return 1;
}
finally
{
    certificate?.Dispose();
    try
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
    catch
    {
        // A locked deployment file can be removed later with the Temp directory.
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static void ExtractResource(string resourceName, string destinationPath)
{
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"安装器缺少嵌入资源：{resourceName}");
    using var destination = File.Create(destinationPath);
    resource.CopyTo(destination);
}
