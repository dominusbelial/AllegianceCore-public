// P824 — MdlTextureDecoder/MdlTexture moved to Allegiance.Core (so the
// mdl→glTF authoring pipeline can decode textures headless); every file in
// this project already used the type unqualified through the old
// Allegiance.ClientCore.MonoGame.Rendering namespace, so a project-wide
// global using keeps that working without touching dozens of files.
global using Allegiance.Core;
