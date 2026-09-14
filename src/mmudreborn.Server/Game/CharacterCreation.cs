using mmudreborn.Data.Models;

namespace mmudreborn.Server;

public class CharacterCreation
{
    private readonly IGameClient _client;
    private readonly GameWorld _world;

    // Stock-faithful cosmetic options
    private static readonly string[] HairLengths = { "None", "Short", "Shoulder-Length", "Long", "Waist-length", "Ankle-Length" };
    private static readonly string[] HairColours = { "Black", "White", "Silver", "Red", "Brown", "Dark-Brown", "Blonde", "Green", "Blue", "Grey" };
    private static readonly string[] EyeColours = { "Black", "Crimson", "Yellow", "Pale-Blue", "Sea-Blue", "Dark-Blue", "Grey-Blue", "Slate-Grey", "Bright-Green", "Forest-Green", "Pale-Green", "Chesnut-Brown", "Dark-Brown", "Hazel", "Violet", "Lavender", "Golden" };

    public CharacterCreation(IGameClient client, GameWorld world)
    {
        _client = client;
        _world = world;
    }

    public async Task<Game.Player?> CreateCharacterAsync(BbsUserAccount account, string? rerollPreviousName, CancellationToken ct)
    {
        if (_world.BbsUserRepo == null)
            throw new InvalidOperationException("BBS user repository is not configured.");

        // Stock-faithful: a character enters its name on the character-creation (stat) screen itself — the
        // "Given Name" field — not at a separate up-front prompt. That field is always EDITABLE on this
        // create/reroll path (only the TRAIN STATS editor locks the name), and it is PRE-FILLED: with the
        // rerolled character's previous name on a reroll, otherwise with the player's BBS account name (as
        // stock does). Either way the player can type over it. Uniqueness/length are validated on the
        // screen's SAVE; the rerolled character was already deleted at reroll time, so re-using the same
        // name still passes the uniqueness check. Nothing is persisted until SAVE, so bailing out leaves no
        // stub behind.
        string name = !string.IsNullOrWhiteSpace(rerollPreviousName)
            ? Game.Player.NormalizeNamePart(rerollPreviousName!)
            : SeedGivenNameFromAccount(account.UserName);

        int raceId = await GetChoice(
            "Please choose your race [ ? for help ] : ",
            _world.Database.Races.Keys.ToList(),
            ct,
            ShowCompactRaceMenuAsync,
            // Stock `?` at the race prompt just re-displays the list,
            // and `? <topic>` routes to the help files. There is no stat table in stock.
            ShowCompactRaceMenuAsync,
            "Choose your race: ",
            requireEnterToConfirm: true);
        if (raceId < 0) return null;
        var selectedRace = _world.Database.Races[raceId];

        int classId = await GetChoice(
            "Please choose your class [ ? for help ] : ",
            _world.Database.Classes.Keys.ToList(),
            ct,
            ShowCompactClassMenuAsync,
            // Stock `?` at the class prompt just re-displays the list,
            // and `? <topic>` routes to the help files. There is no stat table in stock.
            ShowCompactClassMenuAsync,
            "Choose your class: ",
            requireEnterToConfirm: true);
        if (classId < 0) return null;
        var selectedClass = _world.Database.Classes[classId];

        // Choose Gender
        await _client.SendLineAsync();
        await _client.SendLineAsync(MudAnsi.Info("Gender:"));
        await _client.SendLineAsync($"  {MudAnsi.BrightYellow} 1{MudAnsi.Reset}. {MudAnsi.BrightWhite}Male{MudAnsi.Reset}");
        await _client.SendLineAsync($"  {MudAnsi.BrightYellow} 2{MudAnsi.Reset}. {MudAnsi.BrightWhite}Female{MudAnsi.Reset}");
        await _client.SendLineAsync();
        int genderChoice = await GetChoice("Choose your gender: ", [1, 2], ct);
        if (genderChoice < 0) return null;
        int gender = genderChoice - 1; // 0=Male, 1=Female

        // New characters start at their race minimums and spend BaseCP in the editor.
        int str = selectedRace.MinStr;
        int agl = selectedRace.MinAgl;
        int intel = selectedRace.MinInt;
        int wil = selectedRace.MinWil;
        int hea = selectedRace.MinHea;
        int chm = selectedRace.MinChm;

        // Stock-faithful baseline HP is derived from final stats/class/race (not an early pre-edit roll).
        int hp = 1;

        // Mana calculation
        int mana = Game.Player.CalculateMaxMana(selectedClass, 1);

        // Display order: Strength, Intellect, Willpower, Agility, Health, Charm
        string[] statNames = { "Strength", "Intellect", "Willpower", "Agility", "Health", "Charm" };
        int[] displayStats = { str, intel, wil, agl, hea, chm };
        int[] floors = { str, intel, wil, agl, hea, chm };
        int[] raceMins = { selectedRace.MinStr, selectedRace.MinInt, selectedRace.MinWil, selectedRace.MinAgl, selectedRace.MinHea, selectedRace.MinChm };
        int[] maxs = { selectedRace.MaxStr, selectedRace.MaxInt, selectedRace.MaxWil, selectedRace.MaxAgl, selectedRace.MaxHea, selectedRace.MaxChm };

        int cpTotal = selectedRace.BaseCP;
        int cpLeft = cpTotal;

        // Stock order: the lawful Y/N comes AFTER race/class/sex and BEFORE the character-creation
        // (stat) screen — not after it.
        bool? isLawful = await PromptLawfulChoiceAsync(ct);
        if (!isLawful.HasValue)
            return null;

        var result = await RunStatScreen(name, "", selectedRace, selectedClass, displayStats, floors, raceMins, maxs,
            statNames, cpLeft, 0, 0, 0, isCreation: true, nameEditable: true, ct);
        if (result == null) return null;

        (displayStats, cpLeft, int hairLength, int hairColour, int eyeColour, string lastName, name) = result.Value;
        lastName = Game.Player.NormalizeNamePart(lastName.Length > FamilyNameMaxLength ? lastName[..FamilyNameMaxLength] : lastName);

        str = displayStats[0]; intel = displayStats[1]; wil = displayStats[2];
        agl = displayStats[3]; hea = displayStats[4]; chm = displayStats[5];

        // Recompute HP from final edited stats.
        hp = CalculateStartingHp(hea, selectedRace.HPPerLvl, selectedClass.MinHits, selectedClass.MaxHits);

        var player = new Game.Player
        {
            Name = name,
            LastName = lastName,
            PasswordHash = account.PasswordHash,
            BbsUserId = account.UserName,
            IsTestAccount = account.IsTestAccount,
            RaceId = raceId,
            ClassId = classId,
            Level = 1,
            Experience = 0,
            Strength = str,
            Agility = agl,
            Intellect = intel,
            Willpower = wil,
            Health = hea,
            Charm = chm,
            BaseStrength = str,
            BaseAgility = agl,
            BaseIntellect = intel,
            BaseWillpower = wil,
            BaseHealth = hea,
            BaseCharm = chm,
            CurrentHP = hp,
            MaxHP = hp,
            CurrentMana = mana,
            MaxMana = mana,
            CurrentMapNumber = 1,
            CurrentRoomNumber = 2140,
            // Character creation zeroes every coin field and runs BEFORE race/class
            // selection, so stock gives new characters NO starting money. (Was a fabricated 100.)
            Copper = 0,
            // EvilPoints is the canonical alignment field; the legacy
            // Alignment alias derives from it automatically. In stock char-create the lawful Y/N handler
            // seeds EvilPoints = -51 on YES, so a Lawful character
            // STARTS in the Good band (EP < -50) — and the lawful flag then refuses any evil gain, so
            // "Lawful" is permanent Good. A non-lawful character starts neutral (0). Seeding 0 for
            // lawful chars was the bug behind #86 (a Lawful Paladin couldn't cast Protection from Evil,
            // a GOOD-restricted spell, because EP 0 reads as Neutral).
            EvilPoints = isLawful.Value ? -51 : 0,
            IsLawful = isLawful.Value,
            CharacterPoints = cpTotal,
            SpentCP = cpTotal - cpLeft,
            HairLength = hairLength,
            HairColour = hairColour,
            EyeColour = eyeColour,
            Gender = gender,
        };

        player.RecalculateStats(selectedRace, selectedClass, _world.Database);
        player.CurrentMana = player.MaxMana;
        player.RecalculateEquipment(_world.Database);
        // The account→character link is established by the BbsUserId stamped on the player above; the BBS
        // stores no player name, so there is no BBS-side link to write back.
        _world.PlayerRepo.SavePlayer(player);
        await _client.SendAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync(MudAnsi.BrightGreen + "Character created successfully!" + MudAnsi.Reset);
        return player;
    }

    public static int CalculateStartingHp(int health, int raceHpPerLevel, int classField20, int classField22)
    {
        // The stock level-1 secondary-stat baseline:
        // hp ~= floor(health/2) + classMin + classDelta + raceHpPerLvl + trunc(((health-50) * level) / 16)
        int hp = (health / 2) + classField20 + Math.Max(0, classField22) + raceHpPerLevel;
        hp += CalculateTotalHealthHpAdjustment(health, 1);
        return Math.Max(1, hp);
    }

    public static int CalculateTotalHealthHpAdjustment(int health, int level)
    {
        int safeLevel = Math.Max(1, level);
        return ((health - 50) * safeLevel) / 16;
    }

    public static int CalculateHealthHpAdjustmentDelta(int oldHealth, int newHealth, int level)
    {
        return CalculateTotalHealthHpAdjustment(newHealth, level)
            - CalculateTotalHealthHpAdjustment(oldHealth, level);
    }

    public static int RollLevelUpHpGain(Random rng, int health, int previousLevel, int raceHpPerLevel, int classMinHits, int classHitDelta)
    {
        int safePreviousLevel = Math.Max(1, previousLevel);
        int maxRoll = classMinHits + Math.Max(0, classHitDelta);
        int rolledHp = rng.Next(classMinHits, maxRoll + 1);
        int healthDelta = CalculateTotalHealthHpAdjustment(health, safePreviousLevel + 1)
            - CalculateTotalHealthHpAdjustment(health, safePreviousLevel);

        return rolledHp + raceHpPerLevel + healthDelta;
    }

    public async Task<bool> TrainAsync(Game.Player player, CancellationToken ct)
    {
        var race = _world.Database.Races[player.RaceId];
        var cls = _world.Database.Classes[player.ClassId];
        int previousHealth = player.Health;

        string[] statNames = { "Strength", "Intellect", "Willpower", "Agility", "Health", "Charm" };
        int[] displayStats = { player.BaseStrength, player.BaseIntellect, player.BaseWillpower,
                               player.BaseAgility, player.BaseHealth, player.BaseCharm };
        int[] floors = (int[])displayStats.Clone();
        int[] raceMins = { race.MinStr, race.MinInt, race.MinWil, race.MinAgl, race.MinHea, race.MinChm };
        int[] maxs = { race.MaxStr, race.MaxInt, race.MaxWil, race.MaxAgl, race.MaxHea, race.MaxChm };

        int cpLeft = player.CharacterPoints - player.SpentCP;

        string editLastName = player.LastName.Length > FamilyNameMaxLength ? player.LastName[..FamilyNameMaxLength] : player.LastName;

        var result = await RunStatScreen(player.Name, editLastName, race, cls, displayStats, floors, raceMins, maxs,
            statNames, cpLeft, player.HairLength, player.HairColour, player.EyeColour, isCreation: false, nameEditable: false, ct);
        if (result == null) return false;

        (displayStats, cpLeft, var hl, var hc, var ec, var ln, _) = result.Value;

        // The interactive editor above ran OFF the global world gate (GameSession routes "train stats"
        // off-gate so a player sitting in the stat screen doesn't freeze the world — see
        // CommandParser.IsOffGateCommand). The commit below mutates this player's shared, combat-visible
        // state (stats + derived HP), so re-acquire the gate for the in-memory write to preserve the
        // stock single-cooperative-thread invariant against a concurrent combat round.
        await _world.WorldStateGate.WaitAsync(ct);
        Game.GameDiagnostics.MarkGateAcquired("train-commit");
        Action? saveAction = null;
        try
        {
            // The world kept running during the off-gate edit, so the player may have been removed from the
            // realm meanwhile — permadeath on the last life (DeletePlayer) or a dropped connection. Re-saving
            // here would resurrect a deleted character, so bail without committing (same membership guard the
            // write-behind flusher uses). A NON-permadeath death just respawns the player alive at the
            // recovery room; that state is already consistent and the stat changes still apply cleanly.
            if (!_world.IsPlayerOnline(player.Name))
                return false;

            player.BaseStrength = displayStats[0]; player.BaseIntellect = displayStats[1];
            player.BaseWillpower = displayStats[2]; player.BaseAgility = displayStats[3];
            player.BaseHealth = displayStats[4]; player.BaseCharm = displayStats[5];

            player.Strength = player.BaseStrength; player.Intellect = player.BaseIntellect;
            player.Willpower = player.BaseWillpower; player.Agility = player.BaseAgility;
            player.Health = player.BaseHealth; player.Charm = player.BaseCharm;

            player.SpentCP = player.CharacterPoints - cpLeft;
            player.HairLength = hl; player.HairColour = hc; player.EyeColour = ec;
            player.LastName = Game.Player.NormalizeNamePart(ln.Length > FamilyNameMaxLength ? ln[..FamilyNameMaxLength] : ln);

            int hpDelta = CalculateHealthHpAdjustmentDelta(previousHealth, player.Health, player.Level);
            if (hpDelta != 0)
            {
                player.MaxHP = Math.Max(1, player.MaxHP + hpDelta);
                player.CurrentHP = Math.Clamp(player.CurrentHP + hpDelta, 0, player.MaxHP);
            }

            player.RecalculateStats(race, cls, _world.Database);

            // Snapshot the row WHILE the gate is held (CPU-only field reads — consistent against the combat
            // round), then run the DB round-trip after releasing so the write itself never holds the gate.
            saveAction = _world.PlayerRepo.CapturePlayerSave(player);
        }
        finally
        {
            Game.GameDiagnostics.MarkGateReleased();
            _world.WorldStateGate.Release();
        }
        saveAction();

        // Clear the editor screen and return to the realm
        await _client.SendAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync(MudAnsi.BrightGreen + "Training complete! Stats updated." + MudAnsi.Reset);
        _world.BroadcastToRealm(
            $"{MudAnsi.White}{player.Name} just entered the Realm.{MudAnsi.Reset}",
            reprompt: true);
        return true;
    }

    // Cursor rows:
    //  0 = Family Name (text input)
    //  1-6 = Stats (Strength..Charm, left/right adjusts CP)
    //  7-9 = Cosmetics (Hair Length, Hair Colour, Eye Colour — space/left/right toggle)
    //  10 = Exit (toggle SAVE/EDIT/QUIT, Enter to confirm)
    private const int ROW_NAME = 0;       // Given Name — editable on FIRST CREATION only (skipped otherwise)
    private const int ROW_LASTNAME = 1;
    private const int ROW_STAT_FIRST = 2;
    private const int ROW_STAT_LAST = 7;
    private const int ROW_HAIR_LENGTH = 8;
    private const int ROW_HAIR_COLOUR = 9;
    private const int ROW_EYE_COLOUR = 10;
    private const int ROW_EXIT = 11;
    private const int ROW_COUNT = 12;

    // Stock char-name limits: given name max 10, family name max 18, both ALPHA-only.
    private const int GivenNameMinLength = 3;
    private const int GivenNameMaxLength = 10;
    private const int FamilyNameMaxLength = 18;

    // Pre-fill the Given Name field from the BBS login, reduced to what the field itself accepts:
    // ALPHA-only and capped at GivenNameMaxLength. Returns "" if nothing survives
    // (e.g. an all-numeric login), leaving the field blank for the player to type.
    private static string SeedGivenNameFromAccount(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return string.Empty;

        var sb = new System.Text.StringBuilder(GivenNameMaxLength);
        foreach (char c in accountName)
        {
            if (!char.IsLetter(c))
                continue;
            sb.Append(c);
            if (sb.Length >= GivenNameMaxLength)
                break;
        }
        return Game.Player.NormalizeNamePart(sb.ToString());
    }

    // Exit-field multichoice (SAVE/EDIT/QUIT): 0=SAVE, 1=EDIT, 2=QUIT.
    private const int EXIT_SAVE = 0;
    private const int EXIT_EDIT = 1;
    private const int EXIT_QUIT = 2;
    private const int EXIT_CHOICE_COUNT = 3;
    private static readonly string[] ExitChoiceLabels = { "SAVE", "EDIT", "QUIT" };

    /// <summary>
    /// Attempts to apply a typed stat value. Returns an error message if invalid, or null on success.
    /// </summary>
    private string? TryApplyStatInput(string input, int statIndex, int[] stats, int[] floors,
        int[] raceMins, int[] maxs, string[] statNames, ref int cpLeft)
    {
        if (string.IsNullOrEmpty(input))
            return null; // no input, keep current value

        if (!int.TryParse(input, out int newVal))
        {
            return $"{statNames[statIndex]} must be a number.";
        }

        if (newVal > maxs[statIndex])
            return $"{statNames[statIndex]} may not be higher than {maxs[statIndex]}.";
        if (newVal < raceMins[statIndex])
            return $"{statNames[statIndex]} may not be lower than {raceMins[statIndex]}.";
        if (newVal < floors[statIndex])
            return $"{statNames[statIndex]} may not be lower than {floors[statIndex]}.";

        // Calculate CP cost difference between current and new value
        int oldVal = stats[statIndex];
        int cpDelta = CalcTotalCPCost(newVal, raceMins[statIndex]) - CalcTotalCPCost(oldVal, raceMins[statIndex]);
        if (cpDelta > cpLeft)
            return $"You may not assign that much to {statNames[statIndex]}.";

        stats[statIndex] = newVal;
        cpLeft -= cpDelta;
        return null;
    }

    /// <summary>
    /// Total CP cost for a stat value above race minimum.
    /// </summary>
    private static int CalcTotalCPCost(int statValue, int raceMin)
    {
        int total = 0;
        for (int i = 1; i <= statValue - raceMin; i++)
            total += GetCPCost(i);
        return total;
    }

    private async Task<(int[] stats, int cpLeft, int hairLength, int hairColour, int eyeColour, string lastName, string name)?>
        RunStatScreen(string name, string lastName, Race race, CharacterClass cls,
            int[] stats, int[] floors, int[] raceMins, int[] maxs, string[] statNames,
            int cpLeft, int hairLength, int hairColour, int eyeColour, bool isCreation, bool nameEditable, CancellationToken ct)
    {
        int cursorRow = nameEditable ? ROW_NAME : ROW_LASTNAME;

        // Cursor steps over rows, skipping the locked Given Name row when it isn't editable.
        int StepRow(int row, int delta)
        {
            do { row = (row + delta + ROW_COUNT) % ROW_COUNT; }
            while (row == ROW_NAME && !nameEditable);
            return row;
        }
        // The stat-editor exit field, a SAVE/EDIT/QUIT multichoice:
        // 0=SAVE (commit & leave), 1=EDIT (return to editing, no commit/leave), 2=QUIT (discard & leave).
        int exitChoice = 0;
        string errorMessage = "";
        string statInput = ""; // accumulated digit buffer for stat rows

        // Stock field behaviour: a field is "fresh" the moment you arrive on it, so the first character
        // you type REPLACES whatever it held (typing into a stat clears the box via the empty statInput
        // buffer; typing into the Name/Family box clears it the same way). Editing keys (backspace / clear)
        // opt out of the fresh-replace so you can amend the existing value instead. Reset whenever the
        // cursor lands on a different row (see the loop head).
        bool freshField = true;
        int lastFocusedRow = -1;
        // The Given Name's value before this edit — restored (like a rejected stat) if the name the player
        // tries to SAVE doesn't qualify.
        string initialName = name;

        var activePlayer = _client.Player;
        bool restoreSuppressBroadcastOutput = activePlayer?.SuppressBroadcastOutput ?? false;
        bool restoreOutOfRealm = activePlayer?.IsOutOfRealm ?? false;
        if (activePlayer != null)
        {
            activePlayer.SuppressBroadcastOutput = true;

            if (!isCreation)
            {
                // Stock-faithful: entering the TRAIN STATS editor actually pulls the character OUT of the
                // Realm — gone from WHO and the room, untargetable, and skipped by the combat beat / slow
                // tick. The editor runs OFF the world gate (so it can't freeze the world), so without this
                // the character would still be a live, attackable occupant while the player sits in the
                // screen. Restored in the finally below on every exit (save/quit/disconnect).
                activePlayer.IsOutOfRealm = true;

                _world.BroadcastToRealm(
                    $"{MudAnsi.White}{activePlayer.Name} just left the Realm.{MudAnsi.Reset}",
                    reprompt: true);

                // Gone from WHO → also drop from the web online-players roster while training.
                _world.PublishOnlinePresence();

                // Leaving the Realm runs the stock leave-cleanup effects, exactly as a logout
                // (RemovePlayer) does: drop the party (a leader disbands it; a follower leaves), stop any
                // dragging, and dismiss summoned/charmed pets. Without this a TRAIN STATS character stayed
                // a party member/leader while invisible and skipped by every tick — so followers kept
                // "following" a leader who was no longer there and Megamud saw the leader vanish mid-party.
                // These helpers each take their own lock (party / drag / monster), so they're safe on this
                // off-gate path (like the BroadcastToRealm above). The character does NOT auto-rejoin on
                // return — leaving the Realm severs the party, same as a relog.
                _world.RemovePlayerFromParty(activePlayer);
                _world.StopDraggingForPlayer(activePlayer);
                _world.DismissPlayerPets(activePlayer.Name);
            }
        }

        _client.SetEcho(false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Landing on a new row makes its field fresh, so the next character replaces its contents.
                if (cursorRow != lastFocusedRow)
                {
                    freshField = true;
                    lastFocusedRow = cursorRow;
                }

                await DrawStatScreen(name, lastName, race, cls, stats, floors, raceMins, maxs,
                    statNames, cpLeft, hairLength, hairColour, eyeColour, cursorRow, exitChoice,
                    isCreation, nameEditable, errorMessage, statInput);

                var key = await _client.ReadKeyAsync(ct);
                if (key == null)
                    return null;

                // Helper: commit any pending stat input when leaving a stat row.
                // Returns true when the value was accepted or there was nothing to commit.
                bool CommitStatInput(int fromRow)
                {
                    if (fromRow >= ROW_STAT_FIRST && fromRow <= ROW_STAT_LAST && statInput.Length > 0)
                    {
                        int si = fromRow - ROW_STAT_FIRST;
                        var err = TryApplyStatInput(statInput, si, stats, floors, raceMins, maxs, statNames, ref cpLeft);
                        if (err != null)
                        {
                            errorMessage = err;
                            statInput = "";
                            return false;
                        }
                        statInput = "";
                    }

                    return true;
                }

                switch (key)
                {
                    case "Up":
                        CommitStatInput(cursorRow);
                        cursorRow = StepRow(cursorRow, -1);
                        errorMessage = "";
                        break;
                    case "Down":
                        CommitStatInput(cursorRow);
                        cursorRow = StepRow(cursorRow, +1);
                        errorMessage = "";
                        break;
                    case "Left":
                        if (cursorRow == ROW_HAIR_LENGTH) hairLength = (hairLength - 1 + HairLengths.Length) % HairLengths.Length;
                        else if (cursorRow == ROW_HAIR_COLOUR) hairColour = (hairColour - 1 + HairColours.Length) % HairColours.Length;
                        else if (cursorRow == ROW_EYE_COLOUR) eyeColour = (eyeColour - 1 + EyeColours.Length) % EyeColours.Length;
                        else if (cursorRow == ROW_EXIT) exitChoice = (exitChoice + EXIT_CHOICE_COUNT - 1) % EXIT_CHOICE_COUNT;
                        break;
                    case "Right":
                        if (cursorRow == ROW_HAIR_LENGTH) hairLength = (hairLength + 1) % HairLengths.Length;
                        else if (cursorRow == ROW_HAIR_COLOUR) hairColour = (hairColour + 1) % HairColours.Length;
                        else if (cursorRow == ROW_EYE_COLOUR) eyeColour = (eyeColour + 1) % EyeColours.Length;
                        else if (cursorRow == ROW_EXIT) exitChoice = (exitChoice + 1) % EXIT_CHOICE_COUNT;
                        break;
                    case "Space":
                        if (cursorRow == ROW_HAIR_LENGTH) hairLength = (hairLength + 1) % HairLengths.Length;
                        else if (cursorRow == ROW_HAIR_COLOUR) hairColour = (hairColour + 1) % HairColours.Length;
                        else if (cursorRow == ROW_EYE_COLOUR) eyeColour = (eyeColour + 1) % EyeColours.Length;
                        else if (cursorRow == ROW_EXIT) exitChoice = (exitChoice + 1) % EXIT_CHOICE_COUNT;
                        break;
                    case "Enter":
                        if (cursorRow >= ROW_STAT_FIRST && cursorRow <= ROW_STAT_LAST)
                        {
                            // Commit typed stat value.
                            // If validation fails, stay on the same line so the user can retry immediately.
                            bool committed = CommitStatInput(cursorRow);
                            if (committed)
                            {
                                statInput = "";
                                cursorRow = StepRow(cursorRow, +1);
                            }
                        }
                        else if (cursorRow == ROW_EXIT)
                        {
                            if (exitChoice == EXIT_SAVE)
                            {
                                // On a FRESH creation the given name is entered here, so it must be valid
                                // and unique before we leave. On failure the name — like a rejected stat —
                                // reverts to what it was and the cursor returns to the name row so the
                                // player can type a fresh one.
                                if (nameEditable)
                                {
                                    string candidate = Game.Player.NormalizeNamePart(name);
                                    if (candidate.Length < GivenNameMinLength || candidate.Length > GivenNameMaxLength)
                                    {
                                        errorMessage = $"Name must be {GivenNameMinLength}-{GivenNameMaxLength} characters.";
                                        name = initialName;
                                        cursorRow = ROW_NAME;
                                        break;
                                    }
                                    // Stock content guards: ALPHA-only, not a command verb (reserved-word
                                    // exploit guard), and not an exact monster name.
                                    string? nameViolation = CharacterNameRules.FindViolation(
                                        candidate, _world.Database.Monsters.Values.Select(m => m.Name));
                                    if (nameViolation != null)
                                    {
                                        errorMessage = nameViolation;
                                        name = initialName;
                                        cursorRow = ROW_NAME;
                                        break;
                                    }
                                    if (_world.PlayerRepo.PlayerExists(candidate))
                                    {
                                        errorMessage = "That character name is already taken.";
                                        name = initialName;
                                        cursorRow = ROW_NAME;
                                        break;
                                    }
                                    name = candidate;
                                }
                                return (stats, cpLeft, hairLength, hairColour, eyeColour, lastName, name);
                            }
                            if (exitChoice == EXIT_QUIT)
                                return null; // discard without saving
                            if (exitChoice == EXIT_EDIT)
                            {
                                // "EDIT": re-enter the form without committing or leaving. The Given
                                // Name is a pre-filled header the player rarely retouches, so the FSD drops
                                // focus on the Family Name row (the first grid field), and the exit field
                                // snaps back to its SAVE default.
                                cursorRow = ROW_LASTNAME;
                                exitChoice = EXIT_SAVE;
                                errorMessage = "";
                            }
                        }
                        else
                        {
                            // Enter behaves like moving to the next line in editable rows.
                            cursorRow = StepRow(cursorRow, +1);
                        }
                        break;
                    case "Backspace":
                    case "Delete":
                        // Editing keys amend the existing value rather than replacing it, so leaving the
                        // field no longer counts as fresh.
                        freshField = false;
                        if (cursorRow == ROW_NAME && name.Length > 0)
                            name = name[..^1];
                        else if (cursorRow == ROW_LASTNAME && lastName.Length > 0)
                            lastName = lastName[..^1];
                        else if (cursorRow >= ROW_STAT_FIRST && cursorRow <= ROW_STAT_LAST && statInput.Length > 0)
                            statInput = statInput[..^1];
                        break;
                    case "CtrlU":
                        freshField = false;
                        if (cursorRow == ROW_NAME)
                            name = string.Empty;
                        else if (cursorRow == ROW_LASTNAME)
                            lastName = string.Empty;
                        else if (cursorRow >= ROW_STAT_FIRST && cursorRow <= ROW_STAT_LAST)
                            statInput = string.Empty;
                        break;
                    default:
                        // Single character typed
                        if (key.Length == 1)
                        {
                            char c = key[0];
                            if (cursorRow == ROW_NAME)
                            {
                                // Stock char-name validation: ALPHA characters only —
                                // no digits/symbols — and the given name is capped at 10 characters.
                                if (char.IsLetter(c))
                                {
                                    // First keystroke on a freshly-focused field replaces its contents
                                    // (also restart if the field is already full).
                                    if (freshField || name.Length >= GivenNameMaxLength)
                                        name = string.Empty;
                                    freshField = false;
                                    if (name.Length < GivenNameMaxLength)
                                        name += c;
                                    errorMessage = "";
                                }
                            }
                            else if (cursorRow == ROW_LASTNAME)
                            {
                                // Stock char-name validation: family name is ALPHA-only
                                // (no digits/symbols/spaces) and capped at 18 characters.
                                if (char.IsLetter(c))
                                {
                                    // First keystroke on a freshly-focused field replaces its contents
                                    // (also restart if the surname field is already full).
                                    if (freshField || lastName.Length >= FamilyNameMaxLength)
                                        lastName = string.Empty;
                                    freshField = false;
                                    if (lastName.Length < FamilyNameMaxLength)
                                        lastName += c;
                                }
                            }
                            else if (cursorRow >= ROW_STAT_FIRST && cursorRow <= ROW_STAT_LAST)
                            {
                                if (char.IsDigit(c) && statInput.Length < 4)
                                {
                                    statInput += c;
                                    errorMessage = "";
                                }
                            }
                        }
                        break;
                }
            }
        }
        finally
        {
            _client.SetEcho(true);
            if (activePlayer != null)
            {
                activePlayer.SuppressBroadcastOutput = restoreSuppressBroadcastOutput;
                // Re-enter the Realm on EVERY exit path (save/quit/edit-abort/disconnect) so the character
                // is never stranded out of the world. The post-screen commit in TrainAsync re-acquires the
                // world gate, so it can't race a combat beat in the gap before it runs.
                activePlayer.IsOutOfRealm = restoreOutOfRealm;
                // Re-entered the Realm — restore them to the web online-players roster.
                _world.PublishOnlinePresence();
            }
        }

        return null;
    }

    // ── Drawing ───────────────────────────────────────────────────────
    // Reproduces the EDITCHA1 template from WCCTEXT.MSG faithfully.
    // Uses CP437 box-drawing characters — transport host is responsible for CP437 encoding.
    // Fits within 80 columns x 24 lines (standard terminal).

    // CP437 box-drawing constants
    private const char BOX_H = '─';  // CP437 0xC4 horizontal
    private const char BOX_V = '│';  // CP437 0xB3 vertical
    private const char BOX_TL = '┌';  // CP437 0xDA top-left (not used — original uses '.')
    private const char BOX_TR = '┐';  // CP437 0xBF top-right (used in cost chart)
    private const char BOX_BL = '└';  // CP437 0xC0 bottom-left
    private const char BOX_BR = '┘';  // CP437 0xD9 bottom-right
    private const char BOX_LT = '├';  // CP437 0xC3 left-tee
    private const char BOX_RT = '┤';  // CP437 0xB4 right-tee
    private const char BOX_BT = '┴';  // CP437 0xC1 bottom-tee
    private const char BOX_RN = '⌐';  // CP437 0xA9 reversed-not (bottom-left scroll)
    private const char ARR_R = '»';  // CP437 0xAF right guillemet
    private const char ARR_L = '«';  // CP437 0xAE left guillemet
    private const char ARR_LT = '\x11'; // CP437 0x11 left-pointing triangle (connector)

    internal static IReadOnlyList<string> BuildStatScreenLines(
        string name,
        string lastName,
        Race race,
        CharacterClass cls,
        int[] stats,
        int[] raceMins,
        int[] maxs,
        string[] statNames,
        int cpLeft,
        int hairLength,
        int hairColour,
        int eyeColour,
        int cursorRow,
        int exitChoice,
        bool isCreation,
        bool nameEditable = false,
        string errorMessage = "",
        string statInput = "")
    {
        string w = MudAnsi.White;
        string rd = MudAnsi.Red;
        string cy = MudAnsi.Cyan;
        string bw = MudAnsi.BrightWhite;
        string bc = MudAnsi.BrightCyan;
        string dg = MudAnsi.DarkGray;
        string mg = MudAnsi.Magenta;
        string br = MudAnsi.BrightRed;
        string bd = MudAnsi.Bold;
        string rs = MudAnsi.Reset;
        const string bgWhite = "\x1b[47m";
        const string fgBlack = "\x1b[30m";

        string H(int n) => new(BOX_H, n);

        string RenderField(string value, int width, bool selected)
        {
            string trimmed = value.Length > width ? value[..width] : value;
            string padded = trimmed.PadRight(width);
            if (!selected)
                return $"{bw}{padded}{rs}";

            return $"{bgWhite}{fgBlack}{padded}{rs}";
        }

        string ar = $"{rd}{ARR_R}{rs}";
        string al = $"{rd}{ARR_L}{rs}";
        string ear = $"{br}{ARR_R}{rs}";
        string eal = $"{br}{ARR_L}{rs}";

        string subtitle = isCreation ? "Character Creation" : "Character Editor";

        var lines = new List<string>
        {
            $"{rs}    .{H(37)}.{H(2)}.",
            $"   / {bc}M A J O R  M U D {rs}{cy}{subtitle} {w}/    \\{rs}  {dg}{BOX_TL}{BOX_H}    {mg}Point Cost Chart    {dg}{H(1)}{BOX_TR}{rs}",
            $"  {rs}{BOX_V}                                     {BOX_LT}{H(2)}.   {dg}{BOX_V} {dg}{BOX_V}                          {BOX_V}{rs}",
            $"  {rs}{BOX_V} {ar} {cy}Given Name   {(nameEditable ? RenderField(name.Length > 0 ? name : string.Empty, 18, cursorRow == ROW_NAME) : $"{bw}{name,-18}")} {al} {w}{BOX_V}___\\_/  {dg}{BOX_V} {mg}1st {rs}{mg}10 points: {bd}1 {rs}{mg}CP each {dg}{BOX_V}{rs}",
            $"  {rs}{BOX_V} {ar} {cy}Family Name  {RenderField(lastName.Length > 0 ? lastName : string.Empty, 18, cursorRow == ROW_LASTNAME)} {al} {w}{BOX_V}        {dg}{BOX_V} {mg}2nd {rs}{mg}10 points: {bd}2 {rs}{mg}CP each {dg}{BOX_V}{rs}",
            $"  {rs}{BOX_V} {ar} {cy}Race         {w}{race.Name,-18} {al} {w}{BOX_V}        {dg}{BOX_V} {mg}3rd {rs}{mg}10 points: {bd}3 {rs}{mg}CP each {dg}{BOX_V}{rs}",
            $"  {rs}{BOX_V} {ar} {cy}Class        {w}{cls.Name,-18} {al} {w}{BOX_V}        {dg}{BOX_V}     ... and so on ...    {BOX_V}{rs}",
            $"  {rs}{BOX_V}                                     {BOX_V}        {dg}{BOX_V}                          {BOX_V}{rs}"
        };

        string[] rightCost =
        {
            $"{dg}{BOX_V} {rs}{mg}+{bd}10 {rs}{mg}to base stat:  {bd}10 {rs}{mg}CP {dg}{BOX_V}{rs}",
            $"{rs}{mg}+{bd}20 {rs}{mg}to base stat:  {bd}30 {rs}{mg}CP {dg}{BOX_V}{rs}",
            $"{dg}{BOX_V} {rs}{mg}+{bd}30 {rs}{mg}to base stat:  {bd}60 {rs}{mg}CP {dg}{BOX_V}{rs}",
            $"{dg}{BOX_V} {rs}{mg}+{bd}40 {rs}{mg}to base stat: {bd}100 {rs}{mg}CP {dg}{BOX_V}{rs}",
            $"{dg}{BOX_V} {rs}{mg}+{bd}50 {rs}{mg}to base stat: {bd}150 {rs}{mg}CP {dg}{BOX_V}{rs}",
            $"{dg}{BOX_BL}{H(1)}    ... and so on ...   {H(1)}{BOX_BR}{rs}",
        };
        string[] rightConn =
        {
            "        ",
            $" {dg}{H(7)}{BOX_RT}{rs} ",
            "        ",
            "        ",
            "        ",
            "        ",
        };

        for (int i = 0; i < 6; i++)
        {
            string valDisplay = cursorRow == ROW_STAT_FIRST + i && statInput.Length > 0
                ? statInput.PadLeft(4)
                : stats[i].ToString().PadLeft(4);
            string statField = RenderField(valDisplay, 4, cursorRow == ROW_STAT_FIRST + i);
            lines.Add(
                $"  {rs}{BOX_V} {ar} {cy}{statNames[i],-10}{rs} {w}({raceMins[i],4} to {maxs[i],4})  {statField} {al} {w}{BOX_V}{rightConn[i]}{rightCost[i]}");
        }

        lines.Add($"  {rs}{BOX_V}                                     {BOX_V}");

        string[] cosLabels = { "Hair Length   ", "Hair Colour   ", "Eye Colour    " };
        string[] cosValues = { HairLengths[hairLength], HairColours[hairColour], EyeColours[eyeColour] };
        int[] cosRows = { ROW_HAIR_LENGTH, ROW_HAIR_COLOUR, ROW_EYE_COLOUR };
        string[] cosRight =
        {
            $"        {dg}{BOX_TL}{rs} {cy}Use the Space Bar to{rs}",
            $" {dg}{H(7)}{BOX_RT}{rs} {cy}toggle between choices for{rs}",
            $"        {dg}{BOX_BL}{rs} {cy}your physical description{rs}",
        };

        for (int i = 0; i < 3; i++)
        {
            lines.Add(
                $"  {rs}{BOX_V} {ar}  {cy}{cosLabels[i]}{RenderField(cosValues[i], 15, cursorRow == cosRows[i])}  {al} {w}{BOX_V}{cosRight[i]}");
        }

        lines.Add($"  {rs}{BOX_V}                                     {BOX_V}");
        lines.Add(
            $"  {rs}{BOX_V} {ear}  {cy}Exit: {RenderField(ExitChoiceLabels[exitChoice], 4, cursorRow == ROW_EXIT)} {eal}  {ear} {cy}CP Left: {bw}{cpLeft,7} {rs}{rd}{ARR_L} {w}{BOX_V} {dg}{H(8)} {bw}SAVE{cy}, {bw}EDIT {cy}or {bw}QUIT{rs}");
        lines.Add($" {rs}{BOX_RN}{BOX_BT}{H(33)}.     {BOX_V}");
        lines.Add($" \\{new string('_', 33)}\\___/");
        lines.Add(string.IsNullOrEmpty(errorMessage)
            ? string.Empty
            : $"  {br}{errorMessage}{rs}");

        return lines;
    }

    private async Task DrawStatScreen(string name, string lastName, Race race, CharacterClass cls,
        int[] stats, int[] floors, int[] raceMins, int[] maxs, string[] statNames,
        int cpLeft, int hairLength, int hairColour, int eyeColour, int cursorRow,
        int exitChoice, bool isCreation, bool nameEditable, string errorMessage = "", string statInput = "")
    {
        await _client.SendAsync(MudAnsi.ClearScreen);

        foreach (string line in BuildStatScreenLines(
                     name,
                     lastName,
                     race,
                     cls,
                     stats,
                     raceMins,
                     maxs,
                     statNames,
                     cpLeft,
                     hairLength,
                     hairColour,
                     eyeColour,
                     cursorRow,
                     exitChoice,
                     isCreation,
                     nameEditable,
                     errorMessage,
                     statInput))
        {
            await _client.SendLineAsync(line);
        }

        // Position cursor on the active field's value (provides visual selection feedback)
        // Screen lines: 1=top border, 2=header, 3=blank, 4=given, 5=family, 6=race, 7=class,
        //   8=blank, 9-14=stats, 15=blank, 16-18=cosmetics, 19=blank, 20=exit
        int curLine = 0, curCol = 0;
        switch (cursorRow)
        {
            case ROW_NAME:  // Given Name value (editable on first creation only)
                curLine = 4;
                curCol = 20 + Math.Min(name.Length, GivenNameMaxLength);
                break;
            case ROW_LASTNAME:  // Family Name value
                curLine = 5;
                // Keep the real terminal cursor at the logical insertion point.
                // Field starts at column 20 and accepts up to FamilyNameMaxLength characters.
                curCol = 20 + Math.Min(lastName.Length, FamilyNameMaxLength);
                break;
            case >= ROW_STAT_FIRST and <= ROW_STAT_LAST:  // Stat values
                curLine = 9 + (cursorRow - ROW_STAT_FIRST);
                // Keep caret aligned with typed digits while editing this stat.
                // Field starts at column 34 and is 4 chars wide.
                curCol = 34 + (statInput.Length > 0 ? Math.Min(4, statInput.Length) : 0);
                break;
            case ROW_HAIR_LENGTH:
                curLine = 16; curCol = 22;
                break;
            case ROW_HAIR_COLOUR:
                curLine = 17; curCol = 22;
                break;
            case ROW_EYE_COLOUR:
                curLine = 18; curCol = 22;
                break;
            case ROW_EXIT:
                curLine = 20; curCol = 14;  // start of SAVE/EXIT text
                break;
        }
        if (curLine > 0)
            await _client.SendAsync($"\x1b[{curLine};{curCol}H");
    }

    /// <summary>
    /// CP cost for the Nth point above race minimum (1-indexed).
    /// Points 1-10 = 1 CP each, 11-20 = 2 CP each, 21-30 = 3 CP each, etc.
    /// </summary>
    private static int GetCPCost(int pointNumber) => ((pointNumber - 1) / 10) + 1;

    private async Task<int> GetChoice(
        string prompt,
        List<int> validIds,
        CancellationToken ct,
        Func<Task>? menuRenderer = null,
        Func<Task>? helpRenderer = null,
        string? helpPrompt = null,
        bool requireEnterToConfirm = false)
    {
        bool showingHelp = false;
        // After `? <topic>` we render the help file (which clear-screens and boxes itself) and want the
        // prompt to reappear directly beneath it — NOT the list, which would clear-screen away the help.
        // Stock reprints only the prompt line after help; this one-shot flag reproduces that.
        bool suppressMenuOnce = false;
        string? errorMessage = null;
        int maxDigits = validIds.Max(id => id.ToString().Length);

        while (true)
        {
            if (suppressMenuOnce)
                suppressMenuOnce = false;
            else if (showingHelp && helpRenderer != null)
                await helpRenderer();
            else if (menuRenderer != null)
                await menuRenderer();

            if (!string.IsNullOrEmpty(errorMessage))
                await _client.SendLineAsync(MudAnsi.Error(errorMessage));

            string activePrompt = showingHelp && !string.IsNullOrEmpty(helpPrompt)
                ? helpPrompt
                : prompt;
            string typedDigits = string.Empty;
            await RenderChoicePromptAsync(activePrompt, typedDigits);

            bool restartPrompt = false;
            while (!ct.IsCancellationRequested)
            {
                string? key = !requireEnterToConfirm && typedDigits.Length > 0
                    ? await _client.ReadKeyAsync(500, ct)
                    : await _client.ReadKeyAsync(ct);
                if (key == null)
                    return -1;

                if (key == "Timeout")
                {
                    if (TryParseChoice(typedDigits, validIds, out int timedChoice))
                        return timedChoice;

                    errorMessage = "Invalid choice, try again.";
                    restartPrompt = true;
                    break;
                }

                if (key == "Backspace")
                {
                    if (typedDigits.Length > 0)
                    {
                        typedDigits = typedDigits[..^1];
                        await RenderChoicePromptAsync(activePrompt, typedDigits);
                    }
                    continue;
                }

                if (key == "Enter")
                {
                    if (TryParseChoice(typedDigits, validIds, out int enterChoice))
                        return enterChoice;

                    if (typedDigits.Length == 0)
                        continue;

                    errorMessage = "Invalid choice, try again.";
                    restartPrompt = true;
                    break;
                }

                if (key == "?" && helpRenderer != null && typedDigits.Length == 0)
                {
                    // Stock's `?` at the race/class prompt has two paths:
                    // bare `?` redisplays the compact list (display_*_list), while `? <topic>` calls
                    // or the help-file system. Echo the `?` the key-reader swallowed,
                    // then read the rest of the line as the topic.
                    await _client.SendAsync($"{MudAnsi.White}?{MudAnsi.Reset}");
                    string? topicLine = await _client.ReadLineEchoAsync(true, ct);
                    if (topicLine == null)
                        return -1;

                    string topic = topicLine.Trim();
                    if (topic.Length == 0)
                    {
                        showingHelp = true; // bare `?` — redisplay the list
                    }
                    else
                    {
                        await ShowHelpTopicAsync(topic); // `? <topic>` — route to the help files
                        suppressMenuOnce = true;         // keep the help visible; reprint prompt only
                    }
                    errorMessage = null;
                    restartPrompt = true;
                    break;
                }

                if (key.Length == 1 && char.IsDigit(key[0]))
                {
                    if (typedDigits.Length >= maxDigits)
                    {
                        errorMessage = "Invalid choice, try again.";
                        restartPrompt = true;
                        break;
                    }

                    typedDigits += key;
                    await RenderChoicePromptAsync(activePrompt, typedDigits);

                    bool hasPrefixMatches = validIds.Any(id => id.ToString().StartsWith(typedDigits, StringComparison.Ordinal));
                    if (!hasPrefixMatches)
                    {
                        errorMessage = "Invalid choice, try again.";
                        restartPrompt = true;
                        break;
                    }

                    if (!requireEnterToConfirm && TryParseChoice(typedDigits, validIds, out int directChoice))
                    {
                        bool hasLongerMatch = validIds.Any(id =>
                        {
                            string value = id.ToString();
                            return value.Length > typedDigits.Length && value.StartsWith(typedDigits, StringComparison.Ordinal);
                        });

                        if (!hasLongerMatch)
                            return directChoice;
                    }

                    continue;
                }

                errorMessage = "Invalid choice, try again.";
                restartPrompt = true;
                break;
            }

            if (restartPrompt)
                continue;

            return -1;
        }
    }

    private async Task RenderChoicePromptAsync(string prompt, string typedDigits)
    {
        await _client.ClearCurrentLineAsync();
        await _client.SendAsync($"{MudAnsi.Green}{prompt}{MudAnsi.Reset}{MudAnsi.White}{typedDigits}{MudAnsi.Reset}");
    }

    // Stock help lookup: exact-then-prefix against the loaded help files, rendered through
    // the shared HelpTopicRenderer so `? warrior` at the newchar prompt draws the same CP437-bordered
    // class/race page the in-game HELP command produces. No match prints the stock "No help" line.
    private async Task ShowHelpTopicAsync(string topic)
    {
        await _client.SendLineAsync();
        if (HelpTopicRenderer.TryResolveTopic(_world.Database, topic, out var body, out _) && body != null)
            await HelpTopicRenderer.RenderBodyAsync(_client, body);
        else
            await _client.SendLineAsync($"No help available on '{topic}'.");
    }

    private static bool TryParseChoice(string input, List<int> validIds, out int choice)
    {
        if (int.TryParse(input, out choice) && validIds.Contains(choice))
            return true;

        choice = -1;
        return false;
    }

    private async Task<bool?> PromptLawfulChoiceAsync(CancellationToken ct)
    {
        string? errorMessage = null;

        while (!ct.IsCancellationRequested)
        {
            await ShowLawfulChoiceScreenAsync(errorMessage);
            await _client.SendAsync($"{MudAnsi.White}Do you want to be {MudAnsi.BrightWhite}Lawful{MudAnsi.White}?  [Yes/No] {MudAnsi.Reset}");
            string? response = await _client.ReadLineEchoAsync(true, ct);
            if (response == null)
                return null;

            string answer = response.Trim();
            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            errorMessage = "Please answer Y or N.";
        }

        return null;
    }

    // The stock char-create lawful prompt is a hardcoded string (NOT a data textblock — it is absent
    // from wcchelp/wcctext), shown verbatim before the Yes/No. The old code wrongly dumped the
    // "alignment"/"laws" reputation-bands help topic here instead.
    private static readonly string[] LawfulChoiceText =
    [
        "You must now choose if you want to be a truly 'lawful' citizen of the realm.",
        "If you answer YES to this question then you will never be allowed to instigate",
        "any action which would give you evil points, and any player that attacks or",
        "robs from you will receive three times the regular evil points in return. You",
        "can still attack those with a bad reputation. This option is designed",
        "solely for those who want to stay away from the player combat aspects of the",
        "game, and choosing it for any other reason (Item storage, etc...) is strictly",
        "prohibited.",
        "",
        "Remember, this is a very important choice. Once you have chosen Lawfulness,",
        "you may not remove this title, unless you start a new character.",
    ];

    private async Task ShowLawfulChoiceScreenAsync(string? errorMessage = null)
    {
        await _client.SendAsync(MudAnsi.ClearScreen);

        foreach (var line in LawfulChoiceText)
            await _client.SendLineAsync(line.Length == 0 ? string.Empty : $"{MudAnsi.White}{line}{MudAnsi.Reset}");

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            await _client.SendLineAsync();
            await _client.SendLineAsync(MudAnsi.Error(errorMessage));
        }

        await _client.SendLineAsync();
    }

    private async Task ShowCompactRaceMenuAsync()
    {
        await _client.SendAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync($"{MudAnsi.BrightWhite}Please choose a race from the following list:{MudAnsi.Reset}");
        foreach (var race in _world.Database.Races.Values.OrderBy(r => r.Number))
        {
            string spacing = race.Number < 10 ? "  " : " ";
            await _client.SendLineAsync(
                $"{MudAnsi.White}[{MudAnsi.BrightWhite}{race.Number}{MudAnsi.White}]{spacing}{race.Name}{MudAnsi.Reset}");
        }
        await _client.SendLineAsync();
    }

    private async Task ShowCompactClassMenuAsync()
    {
        await _client.SendAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync($"{MudAnsi.BrightWhite}Please choose a class from the following list:{MudAnsi.Reset}");
        foreach (var cls in _world.Database.Classes.Values.OrderBy(c => c.Number))
        {
            string spacing = cls.Number < 10 ? "  " : " ";
            await _client.SendLineAsync(
                $"{MudAnsi.White}[{MudAnsi.BrightWhite}{cls.Number}{MudAnsi.White}]{spacing}{cls.Name}{MudAnsi.Reset}");
        }
        await _client.SendLineAsync();
    }
}
