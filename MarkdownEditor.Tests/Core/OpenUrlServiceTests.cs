using MarkdownEditor.Core;
using Xunit;

namespace MarkdownEditor.Tests.Core;

public class OpenUrlServiceTests
{
    [Fact]
    public void Open_calls_instance_Open_with_url()
    {
        var urls = new List<string>();
        var mock = new MockOpenUrlService(urls);
        var prev = OpenUrlService.Instance;
        try
        {
            OpenUrlService.Instance = mock;
            OpenUrlService.Open("https://example.com");
            Assert.Single(urls);
            Assert.Equal("https://example.com", urls[0]);
        }
        finally
        {
            OpenUrlService.Instance = prev;
        }
    }

    [Fact]
    public void Open_with_non_http_url_is_passed_to_instance_as_is()
    {
        var urls = new List<string>();
        var mock = new MockOpenUrlService(urls);
        var prev = OpenUrlService.Instance;
        try
        {
            OpenUrlService.Instance = mock;
            OpenUrlService.Open("example.com");
            Assert.Single(urls);
            Assert.Equal("example.com", urls[0]);
        }
        finally
        {
            OpenUrlService.Instance = prev;
        }
    }

    [Fact]
    public void Instance_set_null_uses_DefaultOpenUrlService()
    {
        var prev = OpenUrlService.Instance;
        try
        {
            OpenUrlService.Instance = null!;
            var current = OpenUrlService.Instance;
            Assert.NotNull(current);
            Assert.IsType<DefaultOpenUrlService>(current);
        }
        finally
        {
            OpenUrlService.Instance = prev;
        }
    }

    private sealed class MockOpenUrlService : IOpenUrlService
    {
        private readonly List<string> _urls;

        public MockOpenUrlService(List<string> urls) => _urls = urls;

        public void Open(string url) => _urls.Add(url);
    }
}
