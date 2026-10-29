using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nid.Core.Contracts;

namespace Nid.TestSupport;

/// <summary>Shared by the document tests (design §5, §7): uploads and reads of <c>/api/v1/documents</c>.</summary>
public static class DocumentApi
{
    public const string Documents = "/api/v1/documents";
    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");

    /// <summary>A multipart upload; <paramref name="query"/> keeps the old spelling: <c>fileName</c> names the file, the others are form fields.</summary>
    public static Task<HttpResponseMessage> UploadAsync(NidApp app, PersonId actor, byte[] body, string query = "fileName=bail.pdf")
        => app.RawContentAsync(actor, HttpMethod.Post, Documents, Multipart(body, query));

    public static MultipartFormDataContent Multipart(byte[] body, string query)
    {
        var fields = System.Web.HttpUtility.ParseQueryString(query);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(body);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (fields["fileName"] is { } name)
        {
            // filename*, so a name HttpClient would refuse to quote (a quote, a bidi mark) still travels.
            file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=file; filename*=UTF-8''{Uri.EscapeDataString(name)}");
            form.Add(file);
        }
        foreach (var key in fields.AllKeys.Where(k => k != "fileName"))
            form.Add(new StringContent(fields[key]!), key!);
        return form;
    }

    public static async Task<Guid> UploadedAsync(NidApp app, PersonId actor, byte[] body, string query = "fileName=bail.pdf")
    {
        var response = await UploadAsync(app, actor, body, query);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    public static Task<HttpResponseMessage> GetAsync(NidApp app, PersonId actor, Guid id, string suffix = "")
        => app.RawAsync(actor, HttpMethod.Get, $"{Documents}/{id}{suffix}");

    public const string UploadInstant = "2025-10-15T00:00:00Z";
}
