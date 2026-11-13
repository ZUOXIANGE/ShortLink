using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShortLink.Contracts;
using Xunit;

namespace ShortLink.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ProtocolRegressionTests(ApiFactory factory)
{
    [Fact]
    public async Task Unsupported_Api_Version_Should_Use_Envelope_And_Matching_Biz_Header()
    {
        var response = await factory.CreateClient().GetAsync("/api/v9/links");
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BizCodes.ApiVersionUnsupported, envelope!.Code);
        Assert.Equal(
            BizCodes.ApiVersionUnsupported.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers.GetValues("X-Biz-Code").Single());
    }

    [Fact]
    public async Task Ordinary_Bad_Request_Should_Not_Be_Mislabeled_As_Unsupported_Version()
    {
        using var content = new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json");
        var response = await factory.CreateClient().PostAsync("/api/v1/auth/login", content);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions);

        Assert.Equal(BizCodes.InvalidArgument, envelope!.Code);
        Assert.Equal(
            BizCodes.InvalidArgument.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers.GetValues("X-Biz-Code").Single());
    }

    [Fact]
    public async Task Authentication_Error_Should_Expose_Matching_Biz_Header()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/auth/me");
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(ApiFactory.JsonOptions);

        Assert.Equal(BizCodes.Unauthorized, envelope!.Code);
        Assert.Equal(
            BizCodes.Unauthorized.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers.GetValues("X-Biz-Code").Single());
    }
}
