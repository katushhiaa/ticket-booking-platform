using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace TicketBooking.IntegrationTests;

public class AuthTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    private const string Password = "Str0ngPass!";

    private Task<HttpResponseMessage> RegisterAsync(string email) =>
        Client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, fullName = "Test User" });

    [Fact]
    public async Task Register_Login_Me_FullFlow()
    {
        var register = await RegisterAsync("new.user@example.com");
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "new.user@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await ReadJsonAsync(login)).GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("new.user@example.com", (await ReadJsonAsync(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        await RegisterAsync("dup@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync("dup@example.com")).StatusCode);
    }

    [Fact]
    public async Task Login_SeedUser_Succeeds() =>
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "katerina@example.com", password = "Katerina123" })).StatusCode);

    [Fact]
    public async Task Login_WrongPassword_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "katerina@example.com", password = "wrong" })).StatusCode);

    [Fact]
    public async Task Me_WithoutToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/v1/auth/me")).StatusCode);

    [Fact]
    public async Task Me_WithGarbageToken_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(request)).StatusCode);
    }
}
