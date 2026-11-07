namespace ChronoAudit.Core.Tests;

public class ResultTests
{
    [Fact]
    public void Success_HasNoError_AndReadingErrorThrows()
    {
        var result = ChronoAuditResult.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_ExposesCodeMessageAndKind()
    {
        var result = ChronoAuditResult.Failure(ChronoAuditError.NotFound("Export.NotFound", "No such export."));

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal("Export.NotFound", result.Error.Code);
        Assert.Equal("No such export.", result.Error.Message);
        Assert.Equal(ChronoAuditErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public void TypedSuccess_CarriesTheValue()
    {
        var result = ChronoAuditResult.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void TypedFailure_ReadingValueThrows()
    {
        var result = ChronoAuditResult.Failure<int>(ChronoAuditError.Conflict("Export.Conflict", "Changed."));

        Assert.True(result.IsFailure);
        Assert.Equal(ChronoAuditErrorKind.Conflict, result.Error.Kind);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromValue_IsSuccess()
    {
        ChronoAuditResult<string> result = "hello";

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromError_IsFailure()
    {
        ChronoAuditResult<string> result = ChronoAuditError.Forbidden("Analytics.SubjectForbidden", "Needs the All scope.");

        Assert.True(result.IsFailure);
        Assert.Equal("Analytics.SubjectForbidden", result.Error.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(ChronoAuditErrorKind.Validation)]
    [InlineData(ChronoAuditErrorKind.Unauthorized)]
    [InlineData(ChronoAuditErrorKind.Forbidden)]
    [InlineData(ChronoAuditErrorKind.NotFound)]
    [InlineData(ChronoAuditErrorKind.Conflict)]
    [InlineData(ChronoAuditErrorKind.Unprocessable)]
    [InlineData(ChronoAuditErrorKind.Unavailable)]
    public void EachFactory_SetsItsKind(ChronoAuditErrorKind kind)
    {
        var error = kind switch
        {
            ChronoAuditErrorKind.Validation => ChronoAuditError.Validation("A.B", "m"),
            ChronoAuditErrorKind.Unauthorized => ChronoAuditError.Unauthorized("A.B", "m"),
            ChronoAuditErrorKind.Forbidden => ChronoAuditError.Forbidden("A.B", "m"),
            ChronoAuditErrorKind.NotFound => ChronoAuditError.NotFound("A.B", "m"),
            ChronoAuditErrorKind.Conflict => ChronoAuditError.Conflict("A.B", "m"),
            ChronoAuditErrorKind.Unprocessable => ChronoAuditError.Unprocessable("A.B", "m"),
            ChronoAuditErrorKind.Unavailable => ChronoAuditError.Unavailable("A.B", "m"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(kind, error.Kind);
        Assert.Equal("A.B", error.Code);
        Assert.Equal("m", error.Message);
    }

    [Fact]
    public void Failure_WithNullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChronoAuditResult.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => ChronoAuditResult.Failure<int>(null!));
    }

    [Theory]
    [InlineData("", "message")]
    [InlineData("  ", "message")]
    [InlineData("Code.X", "")]
    [InlineData("Code.X", " ")]
    public void Failure_WithEmptyCodeOrMessage_Throws(string code, string message)
    {
        var error = new ChronoAuditError(code, message, ChronoAuditErrorKind.Validation);

        Assert.Throws<ArgumentException>(() => ChronoAuditResult.Failure(error));
        Assert.Throws<ArgumentException>(() => ChronoAuditResult.Failure<int>(error));
    }
}
