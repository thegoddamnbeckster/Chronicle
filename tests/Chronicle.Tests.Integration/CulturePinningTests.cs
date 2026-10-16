using System.Globalization;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    /// <summary>The server must not depend on the host's language (Turkish dotless i, comma decimals, ...).</summary>
    public class CulturePinningTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;
        public CulturePinningTests(ChronicleApiFactory factory) => _factory = factory;

        [Fact]
        public void TheServer_RunsInTheInvariantCulture_WhateverTheHostIsSetTo()
        {
            _ = _factory.CreateClient(); // starts Program
            CultureInfo.DefaultThreadCurrentCulture.Should().Be(CultureInfo.InvariantCulture);
            CultureInfo.DefaultThreadCurrentUICulture.Should().Be(CultureInfo.InvariantCulture);
            "I".ToLower().Should().Be("i");
            1234.5.ToString().Should().Be("1234.5");
        }
    }
}
