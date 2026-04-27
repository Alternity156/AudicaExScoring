## WARNING

This mod conflicts with ScorePercentage and HitScoreVisualizer

## EX Scoring

The idea is to make a new scoring system akin to classic rhythm games. I am still not sure where I want this to go but I am experimenting currently, if you have ideas, let me know on Discord!

## Current implementation
| Target | Score Weight |
|--|--|
|Normal, Aim  | 0-1 |
|Normal, Timing | 0-1 |
|Sustain, Hold | 0-1 |
|Melee, Velocity | 0-1 |
|Chain, Node Aim | 0-0.1 |

There is no multiplier, no base score, only these score weights. They are shown as a percentage.

##Current Version Quirk
Currently, judgement scoring has been implemented and is the system that is turned on when using EX scoring.

## Mod Settings
You can use the ModSettings mod to change the score visuals from Audica style to Ex style.
You can also change the score calculation from Audica to Linear.
Default settings are Audica visuals and Audica calculations.
If you do not have ModSettings, you can grab it [here](https://github.com/octoberU/ModSettings) or manually change settings in [Audica Folder]/UserData/MelonPreferences.cfg .
