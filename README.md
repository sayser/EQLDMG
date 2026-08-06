EQDM - EVERQUEST LEGENDS DAMAGE METER

EQDM is a lightweight, real-time combat log parser and damage meter for EverQuest Legends. It watches the active character log, identifies combat events, and presents damage, healing, mitigation, pet ownership, and ability details in a Windows desktop interface.


PROJECT STATUS

EQDM is under active development. Important results should be compared with the original game log. Please report any log format the parser does not yet recognize.


FEATURES

LIVE ENCOUNTER TRACKING

- Monitors the newest eqlog_*.txt character log in real time.
- Identifies the character and server from the log filename.
- Separates encounters after a kill or 10 seconds of combat inactivity.
- Displays encounter duration, total character damage, encounter DPS, and rolling current DPS.
- Uses a 10-second rolling window for Current DPS.
- Keeps the five most recent completed encounters in Encounter History.
- Returns from a selected historical encounter to the live display when a new fight begins.
- Supports manual encounter reset.


DAMAGE AND OFFENSE

EQDM tracks supported combat log lines for:

- Melee attacks and combat abilities.
- Direct spell damage.
- Damage-over-time ticks.
- Reactive and damage-shield damage.
- Physical critical hits and spell critical hits.
- Misses, spell resists, and fizzles.
- Damage and DPS by combatant and ability.
- Percentage contribution to total encounter damage.

The Offense view includes hits, misses, critical-hit percentage, spell-critical percentage, resisted spells, fizzles, an ability chart, and an ability-by-ability damage table.


HEALING

- Tracks actual and potential healing when both values are available in the log.
- Separates direct heals from heal-over-time ticks.
- Calculates total healing and HPS.
- Tracks healing criticals and healing-critical percentage.
- Provides healing totals by ability.

Healing is associated with an active combat encounter. Out-of-combat healing does not create a damage encounter by itself.


DEFENSE AND MITIGATION

- Tracks incoming damage by source and ability.
- Tracks dodge, parry, block, and riposte outcomes.
- Tracks supported melee and spell absorption outcomes.
- Tracks incoming spell resists.
- Calculates avoidance statistics and displays mitigation outcomes.

EQDM reports the results written to the log. It does not estimate unlogged armor values, resistance rolls, or hidden server-side mitigation.


GROUPS, PETS, AND CHARM

- Detects solo and group state from group-related log messages.
- Tracks known group members and their combat contributions.
- Recognizes conventionally named owned pets and maps their damage to their owner.
- Correlates charm casts with successful "has been charmed" messages.
- Handles failed or resisted charm attempts without assigning ownership.
- Removes or replaces controlled-pet ownership when charm wears off, the owner leaves, or another charm succeeds.
- Preserves valid local charm state across solo and group transitions.
- Keeps damage dealt during a valid charm period attributed to the controlling player after charm breaks.
- Shows pets as separate ranked combatants or combines pet damage into the owner's DPS row.
- Provides an expandable PET DMG ability group so individual pet attacks remain visible.

Charm attribution depends on the order and visibility of the corresponding cast, success, and wear-off messages in the local log. If the game client does not write one of these events, EQDM cannot reconstruct information that was never logged.




GETTING STARTED

PORTABLE RELEASE

The intended distribution format is a self-contained Windows portable ZIP.

1. Download the EQDM ZIP for your Windows architecture.
2. Extract the entire ZIP to a normal writable folder.
3. Run EQLDamageMeter.exe from the extracted folder.
4. If Windows SmartScreen appears for an unsigned development build, review the publisher and file source before deciding whether to run it.

Do not run EQDM directly from inside the ZIP. The application stores settings.json beside the executable, so the extracted folder must be writable if you want the selected log location to be remembered.

A self-contained portable release includes the required .NET runtime. It does not require an installer, registry entries, a Windows service, or a separate .NET installation.


ENABLE EVERQUEST LOGGING

EQDM requires the game client to write combat messages to a character log. In EverQuest Legends, enable logging with this in-game command:

    /log on

The default log folder is:

    C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs

EQDM selects the most recently updated eqlog_*.txt file in that folder. Use the Logs button if your game is installed elsewhere or you want to monitor another Logs folder.


USING THE APPLICATION

1. Start EverQuest Legends and enable logging.
2. Start EQDM and confirm that the header shows LIVE - Monitoring log.
3. Enter combat. The display begins calculating after the first qualifying combat events.
4. Select a player or pet in the ranking to inspect Offense, Defense, and Healing details.
5. Enable Combine pet DPS with owner to roll normal and charmed pet damage into the owner's ranking.
6. Use Overlay for the compact live ranking window.
7. Select a completed fight in Encounter History to review it. A new fight automatically returns the display to live data.
8. Use Reset when you want to discard the current encounter manually.


ACCURACY AND LOG LIMITATIONS

EQDM is a log parser, so its results are only as complete as the messages visible to and recorded by the local game client. Results can be affected by:

- Logging being disabled or interrupted.
- Starting EQDM partway through a fight.
- Combat messages outside the client's logging or visibility range.
- New or changed EverQuest Legends message formats.
- Ambiguous NPC names or multiple entities with identical names.
- Missing charm cast, success, resist, failure, or wear-off messages.
- Operating-system or game-client interruptions while the log is being written.




PRIVACY

EQDM reads EverQuest Legends text logs from the local computer and calculates statistics locally. The current application does not require an account or upload combat logs to a remote service.



DISCLAIMER

EQDM is an unofficial, fan-made utility. It is not affiliated with, endorsed by, or sponsored by Daybreak Game Company LLC. EverQuest and related names and marks are the property of their respective owners.
