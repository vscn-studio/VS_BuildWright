// Runs the user's actual plugin importer with a small stand-in for the Blockbench editor API.
// First run CoreSmoke with BUILDWRIGHT_BLOCKBENCH_FIXTURES pointing at the fixture directory.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire } from 'node:module';

const [pluginDirectory, fixtureDirectory, gameDirectory] = process.argv.slice(2);
assert(pluginDirectory && fixtureDirectory, 'Usage: node tests/BlockbenchPluginSmoke.mjs <plugin> <fixtures> [game]');
const pluginRequire = createRequire(path.join(path.resolve(pluginDirectory), 'package.json'));
const { buildSync } = pluginRequire('esbuild');
const Ajv = pluginRequire('ajv');
const bundle = buildSync({
    stdin: {
        contents: 'export { import_model } from "./src/import_model"; export { get_texture_location } from "./src/util"; export { schema } from "./src/generated/vs_shape_schema.js";',
        resolveDir: path.resolve(pluginDirectory),
    }, bundle: true, platform: 'node', format: 'cjs', write: false,
}).outputFiles[0].text;

const objects = [];
class EditorObject {
    constructor(options) { Object.assign(this, options); this.children = []; objects.push(this); }
    addTo(parent) { this.parent = parent; parent?.children.push(this); return this; }
    init() { return this; }
}
class Group extends EditorObject {}
class Cube extends EditorObject {}
class Texture { static all = []; }
class Property { constructor(_target, _type, name) { this.name = name; } }
const module = { exports: {} };
const sandbox = {
    module, exports: module.exports, require: pluginRequire, console, Group, Cube, Texture, Property,
    ModelProject: class {}, Locator: class {}, CubeFace: class {},
    Settings: { get: key => key === 'game_path' ? gameDirectory : true },
};
vm.runInNewContext(bundle, sandbox, { filename: 'blockbench-plugin-smoke.cjs' });
const exports = module.exports;
const validate = new Ajv().compile(exports.schema);

function rotateAround(point, object) {
    const result = point.map((v, i) => v - object.origin[i]);
    // Blockbench formatVS uses XYZ Euler order, matching the game's Rx * Ry * Rz.
    for (let axis = 2; axis >= 0; axis--) {
        const a = object.rotation[axis] * Math.PI / 180;
        const j = (axis + 1) % 3, k = (axis + 2) % 3;
        const x = result[j], y = result[k];
        result[j] = Math.cos(a) * x - Math.sin(a) * y;
        result[k] = Math.sin(a) * x + Math.cos(a) * y;
    }
    return result.map((v, i) => v + object.origin[i]);
}
function worldCorners(cube) {
    const corners = [];
    for (let mask = 0; mask < 8; mask++) {
        let point = Array.from({ length: 3 }, (_, i) => (mask & (1 << i) ? cube.to : cube.from)[i]);
        for (let object = cube; object; object = object.parent) point = rotateAround(point, object);
        corners.push(point.map((v, i) => (v + (i === 1 ? 0 : 8)) / 16));
    }
    return corners;
}
const near = (a, b) => a.every((v, i) => Math.abs(v - b[i]) < 0.00001);
const fixtures = fs.readdirSync(fixtureDirectory).filter(file => file.endsWith('.json'));
assert(fixtures.length >= 4, 'Expected ordinary blocks, chisel, composite and nested fixtures');
for (const file of fixtures) {
    const { model, vertices } = JSON.parse(fs.readFileSync(path.join(fixtureDirectory, file), 'utf8'));
    assert(validate(model), JSON.stringify(validate.errors));
    objects.length = 0;
    Texture.all = Object.keys(model.textures).map(name => ({ name }));
    exports.import_model(structuredClone(model), false, file);
    const cubes = objects.filter(object => object instanceof Cube);
    assert(cubes.length > 0, 'Plugin produced no editable cubes');
    const actual = cubes.flatMap(worldCorners);
    const expected = Array.from({ length: vertices.length / 3 }, (_, i) => vertices.slice(i * 3, i * 3 + 3));
    assert(actual.every(point => expected.some(other => near(point, other))), file + ': imported corners differ from game geometry');
    assert(expected.every(point => actual.some(other => near(point, other))), file + ': missing imported geometry');
    for (const cube of cubes) {
        for (const face of Object.values(cube.faces)) assert(face.texture, 'Plugin lost a face texture reference');
    }
    if (gameDirectory && file === 'blocks.json') {
        for (const texture of Object.values(model.textures))
            assert(fs.existsSync(exports.get_texture_location(null, texture)), 'Plugin cannot resolve vanilla texture: ' + texture);
    }
    console.log(file + ': plugin schema, editable cubes, hierarchy, pivots and geometry passed');
}
