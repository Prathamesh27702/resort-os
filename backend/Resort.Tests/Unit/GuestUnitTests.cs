using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Domain.Guests;
using Resort.Infrastructure.Protection;

namespace Resort.Tests.Unit;

public class PhoneNormalisationTests
{
    [Theory]
    [InlineData("98200 12345", "+919820012345")]          // Indian mobile typed locally
    [InlineData("098200-12345", "+919820012345")]         // with trunk 0
    [InlineData("91 98200 12345", "+919820012345")]       // country code without +
    [InlineData("+91 (982) 001-2345", "+919820012345")]
    [InlineData("0832 227 1234", "+918322271234")]        // Goa landline with STD code
    [InlineData("+44 20 7946 0958", "+442079460958")]     // UK
    [InlineData("0044 20 7946 0958", "+442079460958")]    // international prefix 00
    [InlineData("+1 (415) 555-0123", "+14155550123")]     // US
    public void Valid_numbers_become_E164(string input, string expected) =>
        Assert.Equal(expected, GuestRules.NormalizePhone(input));

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("5820012345")]             // 10 digits but not an Indian mobile, and no country code
    [InlineData("+91 98200 1234")]         // Indian number one digit short
    [InlineData("98200 12345 ext 2")]      // letters
    [InlineData("+0 1234567")]
    [InlineData("++919820012345")]
    public void Invalid_numbers_are_rejected(string input) => Assert.Null(GuestRules.NormalizePhone(input));

    [Theory]
    [InlineData("98200", "98200")]                       // part of a number: digits as typed
    [InlineData("098200 12345", "919820012345")]         // a whole local number: normalised
    [InlineData("riya", null)]                           // not a phone search
    [InlineData("12", null)]                             // too short to mean anything
    public void Search_terms_that_look_like_phones_become_digits(string term, string? expected) =>
        Assert.Equal(expected, GuestService.PhoneDigitsOf(term));
}

public class GuestRuleTests
{
    [Fact]
    public void Names_compare_without_case_accents_or_extra_spaces() =>
        Assert.Equal(GuestRules.NameKey("Zoë", "D'Souza"), GuestRules.NameKey(" zoe ", "dsouza"));

    [Fact]
    public void ID_numbers_are_normalised_before_hashing_and_masked_to_four_characters()
    {
        Assert.Equal("C01X00T47", GuestRules.NormalizeIdNumber(" c01x-00t 47 "));
        Assert.Equal("0T47", GuestRules.Last4("C01X00T47"));
        Assert.Equal("AB1", GuestRules.Last4("AB1"));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "application/pdf")]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, null)]      // "<html"
    public void File_type_comes_from_the_bytes_not_the_name(byte[] head, string? expected) =>
        Assert.Equal(expected, GuestDocumentService.Sniff(head));

    [Fact]
    public void Uploaded_file_names_are_made_safe()
    {
        Assert.Equal("passport.pdf", GuestDocumentService.SafeFileName("../../etc/passport.exe", "application/pdf"));
        Assert.Equal("document.png", GuestDocumentService.SafeFileName("<>|", "image/png"));
    }
}

public class FieldEncryptionTests
{
    private static AesGcmFieldEncryptor Make(string key, string id = "k1", Dictionary<string, string>? previous = null) =>
        new(Options.Create(new EncryptionOptions { Key = key, ActiveKeyId = id, PreviousKeys = previous ?? [] }));

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trips_and_each_encryption_is_different()
    {
        var e = Make(NewKey());
        var a = e.EncryptString("P1234567", "guest_document:1:number");
        var b = e.EncryptString("P1234567", "guest_document:1:number");
        Assert.NotEqual(a, b);                                         // random nonce
        Assert.Equal("P1234567", e.DecryptString(a, "guest_document:1:number"));
    }

    [Fact]
    public void A_value_moved_to_another_row_or_tampered_with_does_not_decrypt()
    {
        var e = Make(NewKey());
        var c = e.EncryptString("P1234567", "guest_document:1:number");
        Assert.ThrowsAny<CryptographicException>(() => e.Decrypt(c, "guest_document:2:number"));
        c[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => e.Decrypt(c, "guest_document:1:number"));
    }

    [Fact]
    public void After_rotation_old_data_is_still_readable_and_new_data_uses_the_new_key()
    {
        var oldKey = NewKey();
        var before = Make(oldKey, "k1").EncryptString("old secret", "ctx");
        var rotated = Make(NewKey(), "k2", new() { ["k1"] = oldKey });
        Assert.Equal("old secret", rotated.DecryptString(before, "ctx"));
        Assert.Equal("k2", rotated.ActiveKeyId);
        Assert.Throws<CryptographicException>(() => Make(NewKey(), "k3").Decrypt(before, "ctx"));   // k1 not configured
    }

    [Fact]
    public void Missing_or_short_keys_fail_fast_with_a_clear_message()
    {
        Assert.Contains("Encryption__Key", Assert.Throws<InvalidOperationException>(() => Make("")).Message);
        Assert.Contains("32 bytes", Assert.Throws<InvalidOperationException>(() => Make(Convert.ToBase64String(new byte[16]))).Message);
    }

    [Fact]
    public void Lookup_hashes_are_stable_and_purpose_bound()
    {
        var key = NewKey();
        Assert.Equal(Make(key).Hash("P1", "doc"), Make(key).Hash("P1", "doc"));
        Assert.NotEqual(Make(key).Hash("P1", "doc"), Make(key).Hash("P1", "other"));
        Assert.NotEqual(Make(key).Hash("P1", "doc"), Make(NewKey()).Hash("P1", "doc"));
    }
}
