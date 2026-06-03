## WARNING
This mod conflicts with ScorePercentage, HitScoreVisualizer, SongBrowser and Meeps Audica UI Enhancements.

Since this mod is a massive overhaul of the song list menu, I took it upon myself to include pretty much all the Song Browser features within this mod.

## Optional Dependencies
[SongDataLoader](https://github.com/MeepsKitten/Audica-SongDataLoader) (Recommended for full experience) Required to show album art.

[SongRequest](https://github.com/Silzoid/SongRequest) (Requires [TwitchConnectorMod](https://github.com/steglasaurous/twitch-connector-mod)) Full twitch song request support through the folder system.

## EX Scoring
The idea is to make a new scoring system akin to classic rhythm games. I am still not sure where I want this to go but I am experimenting currently, if you have ideas, let me know on Discord!

## Current implementation
Currently, judgement scoring has been implemented and is the system that is turned on when using EX scoring.

The values are currently in testing, you can browse the current values in Judgement.cs.

## Song list overhaul
This mod includes a massive overhaul of the song list where the launch page is now besides the song list.
The launch panel now has a lot of data about the selected song such as target data, intensity graph, heatmap, and I do plan to add more.

## Mod Settings
You can use the ModSettings mod to change the score visuals from Audica style to Ex style.
You can also change the score calculation from Audica to Linear.
Default settings are Audica visuals and Audica calculations.
If you do not have ModSettings, you can grab it [here](https://github.com/octoberU/ModSettings) or manually change settings in [Audica Folder]/UserData/MelonPreferences.cfg .

## Thanks
This mod uses code or concepts from other mods and software such as: 

[SongBrowser](https://github.com/Silzoid/SongBrowser) (Song Downloading, Favorite system, Song deliting, Song Search, Fast Refresh, Playlist system, Random song, EitherHand fix, Song Request support)

[NotReaper](https://github.com/octoberU/NotReaper/tree/2021-upgrade) (Target Icons for song data)

[Meeps Audica UI Enhancements](https://github.com/MeepsKitten/Meeps-Audica-UI-Enhancements) (Album Art)
