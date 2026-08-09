// Stage 5c: the WPR-owned XNA graphics runtime (moved out of FNA) issues every GPU
// operation through the RHI seam in WPR.Xna.Rhi (XnaBackend.Graphics / the Rhi* structs)
// instead of FNA3D. Global so the ~50 relocated Graphics files need no per-file using.
global using WPR.Xna.Rhi;
