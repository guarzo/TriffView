using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// EveWindowTracker.BlankAmbiguousCharacterSelectNames: blanking CharacterName for clients whose
/// "name" is really an unrecognized window title two or more live clients happen to share.
///
/// Lives here rather than in the cross-platform suite under tests/ because
/// <see cref="EveClientWindow"/> is a Windows-side type. This project already carries a
/// ProjectReference to the app and InternalsVisibleTo.
/// </summary>
public class EveWindowTrackerTests
{
    private static EveClientWindow Client(nint handle, string title, string characterName, uint pid = 1000)
        => new(handle, title, characterName, pid, IsMinimized: false, IsForeground: false);

    [Fact]
    public void SharedCatchAllTitleIsBlankedOnBothClients()
    {
        // Neither of these matched "EVE - Name" / "Name - EVE", so CharacterNameFromTitle fell
        // through to the raw title on both - CharacterName == Title on both. Two clients sharing
        // that value cannot be a real name, since EVE cannot log one character in twice.
        var clients = new[]
        {
            Client(1, "EVE Online", "EVE Online"),
            Client(2, "EVE Online", "EVE Online"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("", result[0].CharacterName);
        Assert.Equal("", result[1].CharacterName);
    }

    [Fact]
    public void UnambiguousCatchAllTitleIsLeftAlone()
    {
        // A single client with an odd, unrecognized title keeps the upstream catch-all behaviour:
        // nobody else shares it, so there's no ambiguity to resolve.
        var clients = new[]
        {
            Client(1, "EVE Online", "EVE Online"),
            Client(2, "EVE - Alice", "Alice"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("EVE Online", result[0].CharacterName);
    }

    [Fact]
    public void ARealParsedNameIsNeverBlanked()
    {
        var clients = new[]
        {
            Client(1, "EVE - Bob", "Bob"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("Bob", result[0].CharacterName);
    }

    [Fact]
    public void TwoClientsSharingARealParsedNameAreNotBlanked()
    {
        // A relog: the closing client's window can briefly still be enumerable alongside its
        // replacement, both reporting the same real character name. CharacterName != Title for
        // either of them, so the shared-name condition alone must not trigger blanking.
        var clients = new[]
        {
            Client(1, "EVE - Bob", "Bob"),
            Client(2, "EVE - Bob", "Bob"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("Bob", result[0].CharacterName);
        Assert.Equal("Bob", result[1].CharacterName);
    }

    [Fact]
    public void AClientWithARealNameIsUnaffectedByOthersSharingAnAmbiguousTitle()
    {
        var clients = new[]
        {
            Client(1, "EVE Online", "EVE Online"),
            Client(2, "EVE Online", "EVE Online"),
            Client(3, "EVE - Carol", "Carol"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("", result[0].CharacterName);
        Assert.Equal("", result[1].CharacterName);
        Assert.Equal("Carol", result[2].CharacterName);
    }

    [Fact]
    public void AParsedNameIsNeverBlankedEvenWhenItEqualsAnotherWindowsAmbiguousTitle()
    {
        // Pathological but decisive: two character-select windows share the unrecognized title
        // "EVE Online", while a third client's name was genuinely parsed from "EVE - EVE Online".
        // Membership in the shared-title set alone would blank that parsed name too; re-testing
        // CharacterName == Title is what protects it.
        var clients = new[]
        {
            Client(1, "EVE Online", "EVE Online"),
            Client(2, "EVE Online", "EVE Online"),
            Client(3, "EVE - EVE Online", "EVE Online"),
        };

        var result = EveWindowTracker.BlankAmbiguousCharacterSelectNames(clients);

        Assert.Equal("", result[0].CharacterName);
        Assert.Equal("", result[1].CharacterName);
        Assert.Equal("EVE Online", result[2].CharacterName);
    }
}
