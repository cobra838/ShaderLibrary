# ShaderLibrary
A library for handling both bfsha and bnsh shader binaries capable of loading and saving. 

## GFD shader archives

`GSHFile.VertexShaders`, `PixelShaders`, and `GeometryShaders` enumerate every
program of that stage in file order. Use these enumerations for stage indices
in particle archives. Block identifiers and indices may repeat.

`Shaders` keeps the existing grouping for ordinary vertex/pixel/geometry GSH
files. A repeated stage header starts another entry instead of replacing an
earlier program. These groups do not establish vertex/pixel bindings in an
archive; those bindings belong to the containing effect format.

Local validation against BotW particle resources preserved all 9,193 vertex
and 7,744 pixel programs in 981 GFD archives. Reading and saving 1,962 BNSH
containers preserved the code, control buffers, and reflection bindings of
17,995 variations. This checks container handling, not Wii U/Switch shader
translation or execution in a game.

NVN control parsing includes the six bytes preceding compute dimensions at
0x748. The dimensions were checked against 4,634 BotW compute reflections and
a UAM 1.5 shader compiled with a (32,2,1) workgroup. CompileTool decompilation
uses the control program offset and compute settings; reflection names such
as `gl_FragData[0]` are kept out of GLSL variable declarations. Generated GLSL
can still depend on emulator/NVN interfaces and unsupported compiler features;
decompilation alone does not establish native shader compatibility.
