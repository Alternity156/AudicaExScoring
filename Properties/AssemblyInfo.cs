using System.Resources;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using ExScoringMod;

[assembly: AssemblyTitle(ExScoring.BuildInfo.Name)]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany(ExScoring.BuildInfo.Company)]
[assembly: AssemblyProduct(ExScoring.BuildInfo.Name)]
[assembly: AssemblyCopyright("Created by " + ExScoring.BuildInfo.Author)]
[assembly: AssemblyTrademark(ExScoring.BuildInfo.Company)]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
//[assembly: Guid("")]
[assembly: AssemblyVersion(ExScoring.BuildInfo.Version)]
[assembly: AssemblyFileVersion(ExScoring.BuildInfo.Version)]
[assembly: NeutralResourcesLanguage("en")]
[assembly: MelonInfo(typeof(ExScoring), ExScoring.BuildInfo.Name, ExScoring.BuildInfo.Version, ExScoring.BuildInfo.Author, ExScoring.BuildInfo.DownloadLink)]


// Create and Setup a MelonModGame to mark a Mod as Universal or Compatible with specific Games.
// If no MelonModGameAttribute is found or any of the Values for any MelonModGame on the Mod is null or empty it will be assumed the Mod is Universal.
// Values for MelonModGame can be found in the Game's app.info file or printed at the top of every log directly beneath the Unity version.
[assembly: MelonGame(null, null)]