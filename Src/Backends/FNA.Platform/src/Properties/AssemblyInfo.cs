using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Resources;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("FNA")]
[assembly: AssemblyDescription("Accuracy-focused XNA4 reimplementation for open platforms")]
#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif
[assembly: AssemblyCompany("Ethan \"flibitijibibo\" Lee")]
[assembly: AssemblyProduct("FNA")]
[assembly: AssemblyCopyright("Copyright (c) 2009-2022")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Mark the assembly as CLS compliant so it can be safely used in other .NET languages
[assembly: CLSCompliant(false)]

// WPR: the FNA rendering/audio backend adapters (WPR.Backend.FNA) call FNA's internal native
// bindings directly (notably the internal `FNA3D` P/Invoke class) so FNA's own DllImport resolver
// (FNADllMap) fires for them. This is the RHI-seam implementation path — see FnaGraphicsBackend.
[assembly: InternalsVisibleTo("WPR.Backend.FNA")]
// WPR: same for the audio adapters, which moved to Src/Audio/WPR.Audio.FAudio on 2026-09-01. They
// call the global-namespace FAudio/FACT bindings and FNAPlatform's microphone capture, both of
// which must be invoked from a caller FNA trusts so FNADllMap resolves the native libraries.
[assembly: InternalsVisibleTo("WPR.Audio.FAudio")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this
// project is exposed to COM.
[assembly: Guid("81119db2-82a6-45fb-a366-63a08437b485")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
[assembly: AssemblyVersion("22.08.0.0")]
