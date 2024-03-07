using System.Threading.Tasks;
using Downloader.DummyHttpServer;

namespace Downloader.Tests.UnitTests;

public class DownloadPackageTestOnMemory : DownloadPackageTest
{
    public override async Task InitializeAsync()
    {
        Package = new DownloadPackage() {
            Urls = new[] { DummyFileHelper.GetFileWithNameUrl(DummyFileHelper.SampleFile16KbName, DummyFileHelper.FileSize16Kb) },
            TotalFileSize = DummyFileHelper.FileSize16Kb
        };

        await base.InitializeAsync();
    }
}
