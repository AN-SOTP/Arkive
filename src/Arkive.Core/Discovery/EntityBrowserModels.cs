namespace Arkive.Core.Discovery;

public enum GameEntityKind
{
    PlayableCharacter,
    Npc,
}

public sealed record GameEntityDefinition(
    string DisplayName,
    string NameKey,
    string EntityType,
    long EntityId,
    string Category,
    string Detail,
    long? BaseEntityId,
    string? PrimaryAssetReference);

public sealed record CharacterCustomizationDefinition(
    long PlayerClassId,
    int CategoryId,
    int UiPosition,
    int ObjectUnit,
    string ObjectReference,
    string IconReference,
    int IconIndex,
    string MenuName);

public sealed record GameEntityRecord(
    string DisplayName,
    string NameKey,
    string EntityType,
    long EntityId,
    GameEntityKind Kind,
    string Category,
    string Detail,
    long? BaseEntityId,
    string? PrimaryAssetReference,
    IReadOnlyList<GameAssetAssociation> Associations);

public sealed record CharacterCustomizationOption(
    long PlayerClassId,
    int CategoryId,
    string Category,
    int UiPosition,
    int ObjectUnit,
    string ObjectReference,
    string IconReference,
    int IconIndex,
    string MenuName,
    string Evidence,
    IReadOnlyList<string> CandidateObjectNames);
