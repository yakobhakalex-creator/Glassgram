using System.Text.Json;

namespace Glassgram.Telegram;

internal static class ChatMemberPolicy
{
    internal static MemberCapabilities GetCapabilities(JsonElement currentStatus, JsonElement targetStatus)
    {
        var currentType = Text(currentStatus, "@type");
        var targetType = Text(targetStatus, "@type");
        var owner = targetType == "chatMemberStatusCreator";
        var targetAdministrator = targetType == "chatMemberStatusAdministrator";
        var editableAdministrator = !targetAdministrator || Flag(targetStatus, "can_be_edited");
        var rights = Field(currentStatus, "rights");
        var creator = currentType == "chatMemberStatusCreator";
        var promote = !owner && editableAdministrator && (creator || Flag(rights, "can_promote_members"));
        var restrict = !owner && editableAdministrator && (creator || Flag(rights, "can_restrict_members"));
        var invite = creator || Flag(rights, "can_invite_users");
        return new(invite, promote, restrict, targetAdministrator, targetType == "chatMemberStatusBanned");
    }

    internal static bool IsCurrent(int issuedGeneration, int currentGeneration, long issuedChatId, long currentChatId) =>
        issuedGeneration == currentGeneration && issuedChatId == currentChatId;

    internal static GroupAdminCapabilities GetGroupCapabilities(JsonElement currentStatus)
    {
        var type = Text(currentStatus, "@type");
        var creator = type == "chatMemberStatusCreator";
        var rights = Field(currentStatus, "rights");
        var member = type is "chatMemberStatusCreator" or "chatMemberStatusAdministrator" or "chatMemberStatusMember" or "chatMemberStatusRestricted";
        return new(
            creator || Flag(rights, "can_change_info"),
            creator || Flag(rights, "can_restrict_members"),
            creator || Flag(rights, "can_invite_users"),
            creator,
            member);
    }

    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    private static string Text(JsonElement value, string name) => Field(value, name).ValueKind == JsonValueKind.String ? Field(value, name).GetString() ?? "" : "";
    private static bool Flag(JsonElement value, string name) => Field(value, name).ValueKind is JsonValueKind.True;
}

internal readonly record struct MemberCapabilities(
    bool CanInvite,
    bool CanPromote,
    bool CanRestrict,
    bool IsAdministrator,
    bool IsBanned);

internal readonly record struct GroupAdminCapabilities(
    bool CanChangeInfo,
    bool CanRestrictMembers,
    bool CanInvite,
    bool IsOwner,
    bool IsMember);
