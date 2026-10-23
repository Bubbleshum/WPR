using System.Linq;
using System.Xml.Linq;
using Microsoft.Xna.Framework.GamerServices;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="Gamer.GetPartnerToken"/> must answer what a signed-in WP7 device did, because WPR
/// reports the gamer signed in to LIVE. Games parse it with the WP7 Xbox LIVE sample's extractor,
/// reproduced here as Contract Killer and Battleship ship it.
/// </summary>
public class PartnerTokenTests
{
    private static string ExtractTokenData(string rawPartnerToken)
    {
        string text = "<root>" + rawPartnerToken + "</root>";
        var source = from element in XDocument.Parse(text).Descendants()
                     where element.Name.LocalName == "EncryptedData"
                     select element;
        return source.Single().ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public void The_sample_extractor_finds_exactly_one_EncryptedData()
    {
        string token = Gamer.GetPartnerToken("http://xboxlive.com/");

        string data = ExtractTokenData(token);

        Assert.StartsWith("<xenc:EncryptedData", data);
        Assert.Contains("CipherValue", data);
    }

    [Fact]
    public void Battleships_rewrapped_assertion_still_parses()
    {
        string wrapped = "<EncryptedAssertion xmlns=\"urn:oasis:names:tc:SAML:2.0:assertion\">"
            + ExtractTokenData(Gamer.GetPartnerToken("http://xboxlive.com/")) + "</EncryptedAssertion>";

        Assert.Equal("EncryptedAssertion", XDocument.Parse(wrapped).Root!.Name.LocalName);
    }

    [Fact]
    public void The_async_end_answers_the_same_token()
    {
        Assert.Equal(Gamer.GetPartnerToken("x"), Gamer.EndGetPartnerToken(null!));
    }
}
