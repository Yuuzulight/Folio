# WebGL test correction during #529

The old `WebGlContextIsCapabilityGatedAndTracksBoundedState` test exercised the
simulated WebGL implementation. Several assertions explicitly required behavior
that a native WebGL implementation must reject:

- Getting WebGL1 and WebGL2 on the same canvas both succeeded.
- `bufferSubData` accepted an integer instead of buffer data.
- Shader compilation accepted source based on the presence of `void main`.
- GPU parameters and query results had fixed values unrelated to the device.
- A document with an eight-byte GPU budget still acquired a default drawing buffer.

The replacement checks the generated native binding, typed buffer upload and real
readback, context identity, viewport state, and rejection before over-budget
allocation. Separate page tests compare a triangle, indexed mesh and instanced
draw against independent Chrome PNG references, byte for byte.

Texture, framebuffer, synchronization, query and context-recovery assertions in
the former test did not establish native execution. They must receive genuine
coverage while implementing #530 and #531; their former simulated checks are not
evidence that either issue is complete. The current native page suite has 75
passing cases, including immutable 2D/3D storage, cube maps, array/layer
attachments, framebuffer copies/blits, multisample renderbuffers, integer
textures and samplers, uniform-buffer bindings, transform feedback, all clear
buffer overloads and packed RGB565/RGBA4444/RGBA5551 readback. It also verifies
explicit level-1 sampling after native mipmap generation, generated-level
metadata, mip-level budget refusal, core WebGL2 renderbuffer formats, and
invalid-format and invalid-length upload errors. The broader optional-format
matrix,
mipmap-minification goldens and complete loss/recovery acceptance remain open;
Milestone 6 remains incomplete.

The native integer path was also corrected during the audit: `vertexAttribI4ui`
now reaches ANGLE's unsigned `glVertexAttribI4uiv` entry point, and the expanded
function table is loaded from the native library before context creation. The
Release build and the native-WebGL regressions (including a shader readback of an
unsigned integer vertex constant) pass after that repair.
