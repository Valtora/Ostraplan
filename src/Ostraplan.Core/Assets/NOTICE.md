# classdata.tpk

The Unity class database AssetsTools.NET needs to read a serialized file that carries no
type trees of its own, which is every file in a built game. It describes the layout of the
engine's built-in types per engine version and holds no game data.

Taken from the `ReleaseFiles` folder of [UABEA](https://github.com/nesrak1/UABEA) (MIT),
release v8. Its notes record that the file was dumped with TypeTreeDumper, which is what
lifted the earlier BY-NC-SA licence on the older database.

`NavModArt` embeds it as `Ostraplan.classdata.tpk`. When a game update moves to an engine
version this file does not cover, `NavModArt.Build` reports the version it could not find a
database for, and the fix is a newer copy of this file from the same place.
