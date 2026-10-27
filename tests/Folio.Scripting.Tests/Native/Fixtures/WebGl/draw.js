var canvas = document.getElementById('c');
var kind = typeof contextKind === 'string' ? contextKind : 'webgl2';
var language = typeof shaderLanguage === 'number' ? shaderLanguage : kind === 'webgl' ? 100 : 300;
var gl = canvas.getContext(kind, {antialias:false,depth:false,preserveDrawingBuffer:true});
if (!gl) throw Error('WebGL2 unavailable');
function shader(type, source) {
    var s = gl.createShader(type);
    gl.shaderSource(s, source);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw Error(gl.getShaderInfoLog(s));
    return s;
}
var vertexSource = '#version 300 es\nlayout(location=0) in vec2 p; void main(){ gl_Position=vec4(p,0.0,1.0); }';
if (mode === 'instanced') vertexSource = '#version 300 es\nlayout(location=0) in vec2 p; void main(){ gl_Position=vec4(p.x+float(gl_InstanceID)-0.5,p.y,0.0,1.0); }';
var program = gl.createProgram();
if (language === 100) vertexSource = 'attribute vec2 p; void main(){gl_Position=vec4(p,0.0,1.0);}';
gl.attachShader(program, shader(gl.VERTEX_SHADER, vertexSource));
gl.attachShader(program, shader(gl.FRAGMENT_SHADER, language === 100
    ? 'precision highp float; uniform vec4 color; void main(){gl_FragColor=color;}'
    : '#version 300 es\nprecision highp float; uniform vec4 color; out vec4 pixel; void main(){pixel=color;}'));
gl.bindAttribLocation(program, 0, 'p');
gl.linkProgram(program);
if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw Error(gl.getProgramInfoLog(program));
gl.useProgram(program);
gl.uniform4fv(gl.getUniformLocation(program, 'color'), new Float32Array([1,0.25,0,1]));
if (kind === 'webgl2') {
    var vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
}
var buffer = gl.createBuffer();
gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
var vertices = mode === 'indexed' ? [-0.75,-0.5,0.5,-0.5,0.5,0.75,-0.75,0.75]
    : mode === 'instanced' ? [-0.25,-0.5,0.25,-0.5,0,0.5] : [-0.75,-0.75,0.75,-0.75,0,0.75];
gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(vertices), gl.STATIC_DRAW);
gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
gl.enableVertexAttribArray(0);
gl.viewport(0,0,32,32);
gl.clearColor(0.0625,0.125,0.25,1);
gl.clear(gl.COLOR_BUFFER_BIT);
if (mode === 'indexed') {
    var indices = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indices);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array([0,1,2,0,2,3]), gl.STATIC_DRAW);
    gl.drawElements(gl.TRIANGLES,6,gl.UNSIGNED_SHORT,0);
} else if (mode === 'instanced') {
    gl.drawArraysInstanced(gl.TRIANGLES,0,3,2);
} else {
    gl.drawArrays(gl.TRIANGLES,0,3);
}
if (gl.getError() !== gl.NO_ERROR) throw Error('WebGL error after draw');
