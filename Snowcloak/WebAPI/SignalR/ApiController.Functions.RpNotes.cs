using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.Notes;
using Snowcloak.API.Protocol;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public Task<Dictionary<string, string>> RpNotesOwnProfiles()
    { CheckConnection(); return _snowHub!.InvokeAsync<Dictionary<string, string>>(nameof(RpNotesOwnProfiles)); }
    public Task<RpNotesPage> RpNotesRead(RpNotesReadRequest request)
    { CheckConnection(); return _snowHub!.InvokeAsync<RpNotesPage>(nameof(RpNotesRead), request); }
    public Task<RpNotesWriteReply> RpNotesWrite(RpNotesWriteRequest request)
    {
        CheckConnection();
        request.SupportsRelationships = true;
        return _snowHub!.InvokeAsync<RpNotesWriteReply>(nameof(RpNotesWrite), request);
    }
}
