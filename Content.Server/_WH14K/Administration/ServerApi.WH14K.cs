// Extension file for server admin API so we won't touch SS14 source code much.

using System.Net;
using System.Net.Http;
using Robust.Server.ServerStatus;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Shared.Database;
using Robust.Shared.Network;

// ReSharper disable once CheckNamespace
namespace Content.Server.Administration;

public sealed partial class ServerApi
{
    [Dependency] private readonly IServerDbManager _dbMan = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IBanManager _bans = default!;

    // ReSharper disable once InconsistentNaming
    private void RegisterWH14KHandlers()
    {
        RegisterActorHandler(HttpMethod.Post, "/admin/actions/ban", ActionBan);
        RegisterActorHandler(HttpMethod.Post, "/admin/actions/pardon", ActionPardon);
    }

    private async Task ActionBan(IStatusHandlerContext context, Actor actor)
    {
        var body = await ReadJson<BanActionBody>(context);
        if (body is null)
            return;

        await RunOnMainThread(async () =>
        {
            var playerUserId = new NetUserId(body.PlayerGuid);
            var adminUserId = new NetUserId(actor.Guid);

            var located = await _locator.LookupIdAsync(playerUserId);
            if (located is null)
            {
                await RespondError(context, ErrorCode.PlayerNotFound, HttpStatusCode.NotFound, "Player not found");
                return;
            }

            var locatedAdmin = await _dbMan.GetPlayerRecordByUserId(adminUserId);
            NetUserId? targetAdmin = null;
            if (locatedAdmin is not null)
                targetAdmin = locatedAdmin.UserId;

            var targetHwid = located.LastHWId;
            var targetUid = located.UserId;
            var severity = body.Severity is < 0 or > 3
                ? NoteSeverity.Medium
                : (NoteSeverity)body.Severity;

            _bans.CreateServerBan(
                targetUid,
                body.Username,
                targetAdmin,
                null,
                targetHwid,
                (uint)body.Minutes,
                severity,
                body.Reason);

            _sawmill.Info($"Added server ban for {body.Username} by {FormatLogActor(actor)}");
            await RespondOk(context);
        });
    }

    private async Task ActionPardon(IStatusHandlerContext ctx, Actor actor)
    {
        var body = await ReadJson<PardonActionBody>(ctx);
        if (body is null)
            return;

        await RunOnMainThread(async () =>
        {
            var ban = await _dbMan.GetServerBanAsync((int)body.BanId);
            if (ban is null)
            {
                await RespondError(ctx,
                    ErrorCode.BanNotFound,
                    HttpStatusCode.BadRequest,
                    "Requested ban has been not found");
                return;
            }

            if (ban.Unban is not null)
            {
                await RespondError(ctx, ErrorCode.BadRequest, HttpStatusCode.BadRequest, "Requested ban is already unbanned");
                return;
            }

            await _dbMan.AddServerUnbanAsync(new ServerUnbanDef((int)body.BanId, new NetUserId(actor.Guid), DateTimeOffset.Now));
            _sawmill.Info($"Added server unban of {body.BanId} by {FormatLogActor(actor)}");
            await RespondOk(ctx);
        });
    }

    #region BodyClasses
    private sealed class BanActionBody
    {
        public required string Username { get; set; }
        public required Guid PlayerGuid { get; set; }
        public required string Reason { get; set; }
        public required int Minutes { get; set; }
        public required int Severity { get; set; }
    }

    private sealed class PardonActionBody
    {
        public required long BanId { get; set; }
    }

    #endregion
}
