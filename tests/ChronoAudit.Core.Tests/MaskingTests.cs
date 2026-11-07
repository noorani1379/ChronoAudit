using System.Text;
using System.Text.Json;

namespace ChronoAudit.Core.Tests;

public class MaskingTests
{
    // "mishavad" with a ZWNJ inside; the escapes keep the test independent of editors and code pages.
    private const string PersianWithZwnj = "می‌شود";

    private static SensitiveDataMasker NewMasker(MaskingOptions? options = null) => new(options ?? new MaskingOptions());

    private static BodyMasker NewBodyMasker(MaskingOptions? options = null)
    {
        options ??= new MaskingOptions();
        return new BodyMasker(new SensitiveDataMasker(options), options);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("PASSWORD")]
    [InlineData("newPassword")]
    [InlineData("confirm_password")]
    [InlineData("accessToken")]
    [InlineData("refresh-token")]
    [InlineData("clientSecret")]
    [InlineData("otp")]
    [InlineData("OtpCode")]
    [InlineData("twoFactorCode")]
    [InlineData("apiKey")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("cvv")]
    [InlineData("CVV2")]
    public void SecretNames_AreMasked_WhateverTheCaseOrSeparator(string name)
    {
        Assert.True(NewMasker().IsSecretName(name, codeIsSecret: false));
    }

    [Theory]
    [InlineData("nationalCode")]
    [InlineData("mobile")]
    [InlineData("orderNumber")]
    [InlineData("tokenizer")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryFields_AreKept(string? name)
    {
        Assert.False(NewMasker().IsSecretName(name, codeIsSecret: false));
    }

    [Fact]
    public void Code_IsSecretOnlyUnderSecretPaths()
    {
        var masker = NewMasker();

        Assert.True(masker.IsSecretName("code", codeIsSecret: true));
        Assert.False(masker.IsSecretName("code", codeIsSecret: false));
        Assert.True(masker.CodeIsSecret("/api/v1/identity/auth/verify"));
        Assert.True(masker.CodeIsSecret("/api/v1/identity/OTP/send"));
        Assert.True(masker.CodeIsSecret("/api/v1/identity/sessions/1"));
        Assert.False(masker.CodeIsSecret("/api/v1/shop/orders"));
        Assert.False(masker.CodeIsSecret(null));
    }

    [Fact]
    public void Json_IsMaskedAtAnyDepth_AndKeepsNonAsciiText()
    {
        var json = "{\"name\":\"" + PersianWithZwnj + "\",\"password\":\"p1\",\"nested\":{\"Token\":\"t\",\"items\":[{\"refreshToken\":\"r\",\"ok\":1}]},\"nationalCode\":\"0012345678\"}";

        var masked = NewMasker().MaskJson(json, codeIsSecret: false);

        Assert.Contains(PersianWithZwnj, masked, StringComparison.Ordinal);
        Assert.Contains("0012345678", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("p1", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("\"t\"", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("\"r\"", masked, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"***MASKED***\"", masked, StringComparison.Ordinal);
        Assert.Contains("\"ok\":1", masked, StringComparison.Ordinal);
        using var _ = JsonDocument.Parse(masked);
    }

    [Fact]
    public void BrokenJson_IsMaskedByPattern()
    {
        const string cut = "{\"user\":\"a\",\"password\":\"hunter2\",\"x\":{\"otp\":\"123456\",\"note\":\"abc";

        var masked = NewMasker().MaskJson(cut, codeIsSecret: false);

        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", masked, StringComparison.Ordinal);
        Assert.Contains("\"user\":\"a\"", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutSecretValue_IsStillMasked()
    {
        var masked = NewMasker().MaskText("{\"a\":1,\"password\":\"hunt", codeIsSecret: false);

        Assert.DoesNotContain("hunt", masked, StringComparison.Ordinal);
        Assert.Contains("\"a\":1", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void FormAndQuery_AreMasked()
    {
        var masker = NewMasker();

        Assert.Equal(
            "user=a&Password=***MASKED***&grant=x%20y&refresh_token=***MASKED***",
            masker.MaskForm("user=a&Password=zz&grant=x%20y&refresh_token=tt", false));
        Assert.Equal("?page=2&otp=***MASKED***&code=77", masker.MaskQueryString("?page=2&otp=1234&code=77", codeIsSecret: false));
        Assert.Equal("?code=***MASKED***", masker.MaskQueryString("?code=77", codeIsSecret: true));
        Assert.Null(masker.MaskQueryString(null, false));
        Assert.Equal(string.Empty, masker.MaskQueryString(string.Empty, false));
    }

    [Fact]
    public void AnEncodedSecretKey_InAForm_IsMasked()
    {
        Assert.Equal("a=1&pass%77ord=***MASKED***", NewMasker().MaskForm("a=1&pass%77ord=zz", false));
    }

    [Fact]
    public void ACustomSecretName_AddedToTheOptions_IsMasked()
    {
        var options = new MaskingOptions();
        options.SecretNames.Add("Ssn");
        options.SecretNameSuffixes.Add("pwd");
        var masker = NewMasker(options);

        Assert.True(masker.IsSecretName("ssn", false));
        Assert.True(masker.IsSecretName("userPwd", false));
        Assert.Contains("***MASKED***", masker.MaskJson("{\"SSN\":\"123\"}", false), StringComparison.Ordinal);
        Assert.False(NewMasker().IsSecretName("ssn", false));
    }

    [Fact]
    public void ACustomCodePathFragment_IsHonoured()
    {
        var options = new MaskingOptions();
        options.CodeIsSecretPathFragments.Clear();
        options.CodeIsSecretPathFragments.Add("/pairing/");
        var masker = NewMasker(options);

        Assert.True(masker.CodeIsSecret("/api/pairing/confirm"));
        Assert.False(masker.CodeIsSecret("/api/auth/verify"));
    }

    [Fact]
    public void ABodyOverTheCap_IsTruncated_AndSafe()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"password\":\"x\",\"v\":\"" + new string('ی', 10)).AsSpan(0, 30).ToArray();

        var result = NewBodyMasker().MaskText(bytes, isTruncated: true, "application/json", "/api/v1/things");

        Assert.True(result.IsTruncated);
        Assert.NotNull(result.Text);
        Assert.DoesNotContain("\"x\"", result.Text, StringComparison.Ordinal);
        Assert.Null(result.FileMetadata);
    }

    [Fact]
    public void AWholeJsonBody_IsParsedStructurally_AndANonJsonTypeStillMasksPairs()
    {
        var bodyMasker = NewBodyMasker();
        var json = Encoding.UTF8.GetBytes("{ \"password\" : \"p\", \"a\": 1 }");

        var asJson = bodyMasker.MaskText(json, false, "application/json", "/x");
        var asText = bodyMasker.MaskText(json, false, "text/plain", "/x");
        var asForm = bodyMasker.MaskText(Encoding.UTF8.GetBytes("password=p&a=1"), false, "application/x-www-form-urlencoded", "/x");

        Assert.Equal("{\"password\":\"***MASKED***\",\"a\":1}", asJson.Text);
        Assert.DoesNotContain("\"p\"", asText.Text, StringComparison.Ordinal);
        Assert.Equal("password=***MASKED***&a=1", asForm.Text);
        Assert.False(asJson.IsTruncated);
    }

    [Fact]
    public void ACodeField_IsMaskedOnASecretPath_AndKeptElsewhere()
    {
        var bodyMasker = NewBodyMasker();
        var body = Encoding.UTF8.GetBytes("{\"code\":\"482913\"}");

        Assert.DoesNotContain("482913", bodyMasker.MaskText(body, false, "application/json", "/api/v1/auth/verify").Text, StringComparison.Ordinal);
        Assert.Contains("482913", bodyMasker.MaskText(body, false, "application/json", "/api/v1/things").Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/x-www-form-urlencoded", true)]
    [InlineData("text/plain", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("application/soap+xml", true)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/png", false)]
    [InlineData("multipart/form-data; boundary=x", false)]
    [InlineData(null, false)]
    public void OnlyTextBodies_AreCaptured(string? contentType, bool expected)
    {
        Assert.Equal(expected, BodyMasker.IsCapturable(contentType));
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=x", true)]
    [InlineData("Multipart/Mixed", true)]
    [InlineData("application/json", false)]
    [InlineData(null, false)]
    public void Multipart_IsRecognised(string? contentType, bool expected)
    {
        Assert.Equal(expected, BodyMasker.IsMultipart(contentType));
    }

    [Fact]
    public void Multipart_DescribesMaskedFieldsAndFileMetadataOnly()
    {
        var fields = new[]
        {
            KeyValuePair.Create("nationalCode", "0012345678"),
            KeyValuePair.Create("password", "hunter2"),
            KeyValuePair.Create("tag", "a"),
            KeyValuePair.Create("tag", "b"),
        };
        var files = new[] { ("document", PersianWithZwnj + ".pdf", (string?)"application/pdf", 19L) };

        var result = NewBodyMasker().DescribeForm(fields, files, isTruncated: false, "/api/v1/form");

        Assert.Null(result.Text);
        Assert.False(result.IsTruncated);
        var json = result.FileMetadata!;
        Assert.Contains(PersianWithZwnj + ".pdf", json, StringComparison.Ordinal);
        Assert.Contains("application/pdf", json, StringComparison.Ordinal);
        Assert.Contains("\"size\":19", json, StringComparison.Ordinal);
        Assert.Contains("0012345678", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("\"tag\":[\"a\",\"b\"]", json, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Multipart_OverTheCap_IsCutAndMarkedTruncated()
    {
        var options = new MaskingOptions { MaxBodyBytes = 400 };
        var fields = Enumerable.Range(0, 100).Select(i => KeyValuePair.Create("field" + i, new string('v', 20))).ToArray();

        var result = NewBodyMasker(options).DescribeForm(fields, [], isTruncated: false, "/x");

        Assert.True(result.IsTruncated);
        Assert.Equal(100, result.FileMetadata!.Length);
        Assert.True(Encoding.UTF8.GetByteCount(result.FileMetadata) <= options.MaxBodyBytes);
    }
}
