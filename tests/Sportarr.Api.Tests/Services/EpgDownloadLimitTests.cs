using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class EpgDownloadLimitTests
{
    [Fact]
    public async Task Configured_download_limit_stops_the_http_body()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"epg-download-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        string? spoolPath = null;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dataPath, "config.xml"),
                "<Config><SettingsUpgradeLevel>999</SettingsUpgradeLevel><EpgMaxDownloadSizeMb>1</EpgMaxDownloadSizeMb></Config>");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sportarr:DataPath"] = dataPath
                })
                .Build();
            var configService = new ConfigService(configuration, NullLogger<ConfigService>.Instance);
            using var services = new ServiceCollection()
                .AddSingleton(configService)
                .AddSingleton<IHttpClientFactory>(new FixedBodyHttpClientFactory(2L * 1024 * 1024))
                .AddSingleton<ILogger<XmltvParserService>>(NullLogger<XmltvParserService>.Instance)
                .BuildServiceProvider();
            var parser = ActivatorUtilities.CreateInstance<XmltvParserService>(services);

            var result = await parser.SpoolFromUrlAsync("https://provider.example/guide.xml");
            spoolPath = result.FilePath;

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("1 MB download limit");
        }
        finally
        {
            if (spoolPath != null)
            {
                File.Delete(spoolPath);
            }

            Directory.Delete(dataPath, recursive: true);
        }
    }

    private sealed class FixedBodyHttpClientFactory(long bodyLength) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixedBodyHandler(bodyLength));
    }

    private sealed class FixedBodyHandler(long bodyLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FixedLengthStream(bodyLength))
            });
        }
    }

    private sealed class FixedLengthStream : Stream
    {
        private readonly long _length;
        private long _remaining;

        public FixedLengthStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, bytesRead);
            _remaining -= bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..bytesRead].Clear();
            _remaining -= bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
