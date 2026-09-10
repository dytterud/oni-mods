// ONI's Assembly-CSharp puts several type names in the global namespace that collide with BCL
// types - `DateTime` (the in-game clock UI), `Action`, `Directory` (KMod). Alias the BCL ones so
// harness code doesn't have to fully-qualify everywhere.
global using Directory = System.IO.Directory;
global using SysAction = System.Action;
global using SysDateTime = System.DateTime;
