// SansPost — Entrance 3D (Sprint 14D): główny renderer wejścia do Saloonu na urządzeniach z wydajnym GPU.
// CSS/SVG (Entrance.razor + app.css) zostaje fallbackiem: brak WebGL2, programowy WebGL, słaba płynność pierwszych klatek,
// reduced motion albo błąd inicjalizacji. Three.js ładuje się dopiero po decyzji "3d" — urządzenia z fallbackiem go nie pobierają.
// Scena jest dekoracją (canvas aria-hidden). Nagłówek, tekst i akcje zostają HTML-em nad sceną; JS tylko ustawia ich
// położenie (projekcja punktów tablic ogłoszeń przy drzwiach) albo układ "stack" na wąskich ekranach.
// Kamera w spoczynku stoi (bez reakcji na kursor); klatka jest rysowana tylko przy zmianie (resize, motyw, wejście).
// Decyzja renderera, tekstury, scalanie geometrii i sprzątanie: js/scene3d-common.js (wspólne dla scen 3D).

import {
    THREE, startScene, theme, random,
    clamp01, phase, easeInOutCubic, easeOut, createRenderer, disposeScene, canvasTexture, boardsTexture, noiseTexture,
    wallpaperTexture, signTexture, signMesh as sign, Batch, worldUv
} from "./scene3d-common.js";

export { chooseRenderer } from "./scene3d-common.js";

let state = null;
let generation = 0;

// ---- Paleta i stałe sceny -----------------------------------------------------------------------------------------

const PALETTE = {
    light: { skyTop: "#8fa9c6", skyMid: "#efb07c", skyHorizon: "#fbe0b5", signGlow: 0.1, townSignGlow: 0.05, fog: "#f1cfa3", fogNear: 45, fogFar: 230,
             sun: "#ffd3a0", sunIntensity: 2.7, sunPosition: [-20, 15, 24], hemiSky: "#ffe4c4", hemiGround: "#7a4a2a", hemi: 0.95,
             exposure: 1.02, glass: "#44505a", glassGlow: 0.12, saloonGlow: 0.55, lantern: 0, lanternGlow: 0.6,
             interior: 5, spot: 3, stars: false },
    dark:  { skyTop: "#050914", skyMid: "#141a33", skyHorizon: "#2c2135", signGlow: 0.65, townSignGlow: 0.14, fog: "#1a1523", fogNear: 30, fogFar: 175,
             sun: "#9fb2ff", sunIntensity: 0.85, sunPosition: [18, 24, 16], hemiSky: "#4a5684", hemiGround: "#1d140d", hemi: 0.7,
             exposure: 1.0, glass: "#2a2f3a", glassGlow: 1.35, saloonGlow: 1.9, lantern: 7, lanternGlow: 3.2,
             interior: 8, spot: 7, stars: true }
};

const SALOON_W = 12.4;          // szerokość fasady
const SALOON_TOP = 9.45;        // najwyższy punkt fałszywej fasady
const DOOR_W = 2.1;
const DOOR_OPEN = 80 * Math.PI / 180;
const PLAQUE_W = 2.35;          // szerokość tabliczki (jednostki świata) — rozmiar HTML liczony z projekcji
const ANCHORS = {
    login: [-2.45, 2.5, 0.14],
    register: [-2.45, 1.55, 0.14],
    guest: [2.45, 2.02, 0.14]
};
const END_POSITION = [0, 1.75, -3.4];     // koniec przejazdu: za progiem, w środku saloonu
const END_TARGET = [0, 1.62, -12];

function easeOutBack(x) { const c = 1.4; return 1 + (c + 1) * Math.pow(x - 1, 3) + c * Math.pow(x - 1, 2); }

function brickTexture() {
    return canvasTexture(256, 256, (ctx, w, h) => {
        const rnd = random(11);
        ctx.fillStyle = "rgb(120,110,100)";
        ctx.fillRect(0, 0, w, h);
        const rows = 16, cols = 4;
        for (let r = 0; r < rows; r++) {
            for (let c = -1; c < cols; c++) {
                const t = 190 + Math.round((rnd() - 0.5) * 50);
                ctx.fillStyle = `rgb(${t},${t},${t})`;
                const x = (c + (r % 2) * 0.5) * (w / cols);
                ctx.fillRect(x + 2, r * (h / rows) + 2, w / cols - 4, h / rows - 3);
            }
        }
    });
}

function shinglesTexture() {
    return canvasTexture(256, 256, (ctx, w, h) => {
        const rnd = random(5);
        for (let r = 0; r < 10; r++) {
            for (let c = -1; c < 9; c++) {
                const t = 170 + Math.round((rnd() - 0.5) * 60);
                ctx.fillStyle = `rgb(${t},${t},${t})`;
                const x = (c + (r % 2) * 0.5) * (w / 8);
                ctx.fillRect(x + 1, r * (h / 10), w / 8 - 2, h / 10 - 2);
            }
        }
    });
}

// Tablica ogłoszeń: kilka kartek w rogach (środek zajmują tabliczki HTML na desktopie).
function notesTexture(seed) {
    return canvasTexture(256, 220, (ctx, w, h) => {
        const rnd = random(seed);
        ctx.fillStyle = "#3b2414";
        ctx.fillRect(0, 0, w, h);
        for (const [x, y, nw, nh] of [[14, 12, 70, 54], [178, 18, 62, 70], [22, 150, 56, 58], [180, 146, 60, 56]]) {
            ctx.save();
            ctx.translate(x + nw / 2, y + nh / 2);
            ctx.rotate((rnd() - 0.5) * 0.18);
            ctx.fillStyle = "#e9dcc0";
            ctx.fillRect(-nw / 2, -nh / 2, nw, nh);
            ctx.fillStyle = "rgba(60,40,24,0.55)";
            for (let l = 0; l < 4; l++) ctx.fillRect(-nw / 2 + 8, -nh / 2 + 12 + l * 10, nw - 16 - rnd() * 18, 2);
            ctx.fillStyle = "#a3342a";
            ctx.beginPath(); ctx.arc(0, -nh / 2 + 5, 3, 0, Math.PI * 2); ctx.fill();
            ctx.restore();
        }
    }, false);
}

// Malowany szyld (tekst na desce). Font nagłówków strony — po document.fonts.ready.

// ---- Materiały ----------------------------------------------------------------------------------------------------

function materials() {
    const vertical = boardsTexture(3);
    const siding = boardsTexture(7, { boards: 10, horizontal: true });
    const floor = boardsTexture(9, { boards: 8, horizontal: true, staggered: true });
    const std = (color, map, tile, extra = {}) => {
        const material = new THREE.MeshStandardMaterial({ color, map, roughness: 0.86, metalness: 0, ...extra });
        material.userData.tile = tile;
        return material;
    };
    const glow = color => new THREE.MeshStandardMaterial({ color, emissive: "#ffc877", emissiveIntensity: 1, roughness: 0.35 });

    return {
        saloon: std("#a0613a", vertical, 2),
        saloonSide: std("#8a5334", siding, 2),
        trim: std("#5c3520", vertical, 1),
        porch: std("#8d6341", floor, 3),
        door: std("#7c4a2a", vertical, 1),
        board: std("#6b4428", vertical, 1),
        paints: [std("#c49a5c", siding, 2), std("#8f9a73", siding, 2), std("#7f8f98", siding, 2), std("#a4604d", siding, 2), std("#a28d73", siding, 2)],
        townTrim: std("#5a412d", vertical, 1),
        brick: std("#a45a3c", brickTexture(), 2),
        roof: std("#5b4636", shinglesTexture(), 2),
        metal: std("#2d2621", null, 1, { roughness: 0.55, metalness: 0.4 }),
        brass: std("#b98a3e", null, 1, { roughness: 0.35, metalness: 0.7 }),
        sand: std("#d2a676", noiseTexture(1, 190, 1400), 10),
        mesa: std("#bf7a50", null, 1, { flatShading: true }),
        glass: glow("#44505a"),
        saloonGlass: glow("#51402f"),
        bulb: glow("#fff1cf"),
        wallpaper: std("#7a2d22", wallpaperTexture(), 1.6),
        wainscot: std("#5a3420", vertical, 1),
        floor: std("#7c5234", floor, 3),
        bar: std("#4e2b17", vertical, 1),
        mirror: std("#3a3c44", null, 1, { roughness: 0.45, metalness: 0.35 }),
        bottles: [std("#3f6b3a", null, 1, { roughness: 0.25 }), std("#8a4b1c", null, 1, { roughness: 0.25 }), std("#2d4f6b", null, 1, { roughness: 0.25 })]
    };
}

// ---- Budowa sceny -------------------------------------------------------------------------------------------------

function windowWithFrame(b, glass, frame, x, y, z, w, h) {
    const t = 0.12;
    b.box(glass, w, h, 0.04, x, y, z);
    b.box(frame, w + t * 2, t, 0.12, x, y + h / 2 + t / 2, z + 0.03);
    b.box(frame, w + t * 2.6, t * 1.2, 0.2, x, y - h / 2 - t / 2, z + 0.06);   // parapet
    b.box(frame, t, h, 0.12, x - w / 2 - t / 2, y, z + 0.03);
    b.box(frame, t, h, 0.12, x + w / 2 + t / 2, y, z + 0.03);
    b.box(frame, 0.05, h, 0.06, x, y, z + 0.03);                              // szprosy
    b.box(frame, w, 0.05, 0.06, x, y + h * 0.1, z + 0.03);
}

function buildSaloon(root, b, M, font) {
    const half = SALOON_W / 2, wall = 7.2, porchY = 0.45, doorTop = 3.4;

    // Ściana frontowa wokół otworu drzwi + boki + dach (niewidoczny zza fałszywej fasady).
    b.box(M.saloon, half - DOOR_W / 2, wall, 0.25, -(half + DOOR_W / 2) / 2, wall / 2, -0.125);
    b.box(M.saloon, half - DOOR_W / 2, wall, 0.25, (half + DOOR_W / 2) / 2, wall / 2, -0.125);
    b.box(M.saloon, DOOR_W, wall - doorTop, 0.25, 0, (wall + doorTop) / 2, -0.125);
    for (const side of [-1, 1]) b.box(M.saloonSide, 0.25, wall, 10, side * (half - 0.125), wall / 2, -5);
    b.box(M.trim, SALOON_W, 0.2, 10, 0, wall - 0.1, -5);

    // Fałszywa fasada ze stopniem i gzymsami.
    b.box(M.saloon, SALOON_W, 0.9, 0.3, 0, wall + 0.45, -0.15);
    b.box(M.saloon, 7.8, SALOON_TOP - 8.1 - 0.15, 0.3, 0, (8.1 + SALOON_TOP - 0.15) / 2, -0.15);
    b.box(M.trim, SALOON_W + 0.5, 0.22, 0.5, 0, wall, 0.1);
    b.box(M.trim, SALOON_W + 0.3, 0.16, 0.42, 0, 8.14, 0.05);
    b.box(M.trim, 8.2, 0.2, 0.46, 0, SALOON_TOP - 0.05, 0.05);
    for (let x = -5.6; x <= 5.61; x += 1.4) b.box(M.trim, 0.14, 0.34, 0.3, x, wall - 0.25, 0.1);   // wsporniki gzymsu

    // Szyld: "SALOON" + nazwa (prawdziwy nagłówek h1 zostaje w HTML).
    const board = sign(signTexture("SansPost", { width: 1024, height: 200, kicker: "Saloon", bg: "#22140b", fg: "#f6e6c6", border: "#c99a52", font }), 6.8, 1.33, "main");
    board.position.set(0, 8.72, 0.12);
    root.add(board);
    b.box(M.trim, 7.1, 1.6, 0.12, 0, 8.72, 0.02);

    // Obramowanie drzwi.
    b.box(M.trim, 0.16, doorTop - porchY + 0.1, 0.18, -DOOR_W / 2 - 0.08, (doorTop + porchY) / 2, 0.02);
    b.box(M.trim, 0.16, doorTop - porchY + 0.1, 0.18, DOOR_W / 2 + 0.08, (doorTop + porchY) / 2, 0.02);
    b.box(M.trim, DOOR_W + 0.5, 0.22, 0.22, 0, doorTop + 0.1, 0.03);

    // Okna parteru i piętra (ramy, parapety, szprosy), okiennice, drzwi balkonowe.
    for (const side of [-1, 1]) {
        windowWithFrame(b, M.saloonGlass, M.trim, side * 5.0, 2.15, 0.02, 1.25, 1.85);
        windowWithFrame(b, M.saloonGlass, M.trim, side * 3.6, 5.45, 0.02, 1.3, 1.75);
        b.box(M.door, 0.55, 1.95, 0.05, side * (3.6 + 1.05), 5.45, 0.03);
    }
    b.box(M.door, 1.15, 2.3, 0.06, 0, 5.3, 0.03);
    b.box(M.trim, 1.45, 0.16, 0.14, 0, 6.52, 0.05);

    // Tablice ogłoszeń przy drzwiach (na desktopie wiszą na nich tabliczki HTML).
    for (const side of [-1, 1]) {
        b.box(M.trim, 2.85, 2.35, 0.1, side * 2.45, 2.03, 0.05);
        const notes = sign(notesTexture(side > 0 ? 21 : 17), 2.6, 2.1, "notes");
        notes.position.set(side * 2.45, 2.03, 0.105);
        root.add(notes);
    }

    // Ganek: podłoga, stopnie, słupy, balkon z balustradą, latarnie.
    b.box(M.porch, SALOON_W + 0.8, porchY, 3.3, 0, porchY / 2, 1.65);
    b.box(M.trim, SALOON_W + 0.8, 0.1, 0.1, 0, porchY - 0.05, 3.32);
    b.box(M.porch, 3.4, 0.3, 0.5, 0, 0.15, 3.55);
    b.box(M.porch, 3.4, 0.15, 0.5, 0, 0.075, 4.05);
    for (const x of [-6.3, -3.3, 3.3, 6.3]) {
        b.box(M.trim, 0.24, 3.45, 0.24, x, porchY + 1.725, 3.15);
        b.box(M.trim, 0.34, 0.2, 0.34, x, porchY + 0.1, 3.15);
        b.box(M.trim, 0.5, 0.12, 0.3, x, 3.84, 3.15);
    }
    b.box(M.porch, SALOON_W + 0.8, 0.3, 3.45, 0, 4.05, 1.7);
    b.box(M.trim, SALOON_W + 0.8, 0.45, 0.1, 0, 3.98, 3.45);
    b.box(M.trim, SALOON_W + 0.8, 0.1, 0.12, 0, 5.1, 3.3);
    b.box(M.trim, SALOON_W + 0.8, 0.08, 0.1, 0, 4.36, 3.3);
    for (let x = -6.45; x <= 6.46; x += 0.33) b.box(M.trim, 0.06, 0.72, 0.06, x, 4.72, 3.3);
    for (const side of [-1, 1]) b.box(M.trim, 0.1, 0.1, 3.3, side * 6.55, 5.1, 1.65);
    for (const x of [-3.3, 3.3]) {
        b.box(M.metal, 0.26, 0.36, 0.26, x, 3.3, 3.38);
        b.box(M.bulb, 0.16, 0.24, 0.16, x, 3.3, 3.38);
    }

    // Wnętrze: podłoga, ściany (tapeta + boazeria), bar, lustro, butelki, stoliki, żyrandol.
    b.box(M.floor, 11.8, 0.05, 9.6, 0, porchY - 0.02, -5.05);
    b.box(M.wallpaper, 11.8, 3.5, 0.1, 0, 2.2, -9.8);
    for (const side of [-1, 1]) b.box(M.wallpaper, 0.1, 3.5, 9.6, side * 5.85, 2.2, -5.05);
    b.box(M.trim, 11.8, 0.12, 9.6, 0, 3.9, -5.05);
    b.box(M.wainscot, 11.7, 1.1, 0.05, 0, porchY + 0.55, -9.72);
    for (const side of [-1, 1]) b.box(M.wainscot, 0.05, 1.1, 9.5, side * 5.78, porchY + 0.55, -5.05);
    b.box(M.bar, 6.2, 1.1, 0.8, 0, porchY + 0.55, -7.3);
    b.box(M.trim, 6.5, 0.08, 1.0, 0, porchY + 1.14, -7.3);
    b.box(M.brass, 6.2, 0.05, 0.05, 0, porchY + 0.15, -6.85);
    b.box(M.mirror, 5.6, 1.3, 0.04, 0, 2.55, -9.72);
    for (const y of [1.95, 3.27]) b.box(M.trim, 6.6, 0.06, 0.35, 0, y, -9.6);
    for (let i = 0; i < 22; i++) {
        const row = i < 11 ? 0 : 1, x = -2.7 + (i % 11) * 0.54;
        b.add(M.bottles[i % 3], new THREE.CylinderGeometry(0.06, 0.07, 0.34, 8), x, (row ? 3.27 : 1.95) + 0.2, -9.58);
    }
    for (const [x, z] of [[-2.8, -4.2], [3.0, -5.0], [-3.6, -6.9]]) {
        b.add(M.bar, new THREE.CylinderGeometry(0.62, 0.62, 0.06, 20), x, porchY + 0.8, z);
        b.add(M.bar, new THREE.CylinderGeometry(0.07, 0.12, 0.8, 8), x, porchY + 0.4, z);
        for (const dx of [-0.85, 0.85]) {
            b.box(M.door, 0.46, 0.06, 0.46, x + dx, porchY + 0.48, z);
            b.box(M.door, 0.06, 0.55, 0.46, x + dx + Math.sign(dx) * 0.2, porchY + 0.78, z);
            b.box(M.door, 0.07, 0.45, 0.07, x + dx, porchY + 0.23, z);
        }
    }
    b.add(M.brass, new THREE.TorusGeometry(0.55, 0.035, 6, 24), 0, 3.3, -4.4, Math.PI / 2);
    b.add(M.brass, new THREE.CylinderGeometry(0.02, 0.02, 0.5, 4), 0, 3.6, -4.4);
    for (let i = 0; i < 6; i++) {
        const a = i / 6 * Math.PI * 2;
        b.add(M.bulb, new THREE.SphereGeometry(0.08, 10, 8), Math.cos(a) * 0.55, 3.4, -4.4 + Math.sin(a) * 0.55);
    }
    for (const x of [-3.6, 3.6]) b.add(M.bulb, new THREE.SphereGeometry(0.1, 10, 8), x, 2.9, -9.6);

    // Drzwi wahadłowe: każde skrzydło na własnym pivocie (zawias przy framudze), ażurowe listwy, łuk u góry.
    const doors = [];
    for (const side of [-1, 1]) {
        const pivot = new THREE.Group();
        pivot.position.set(side * (DOOR_W / 2 - 0.02), 0, -0.08);
        const leaf = new Batch();
        const cx = -side * 0.52, bottom = porchY + 0.55, height = 1.4;
        leaf.box(M.door, 0.1, height, 0.07, -side * 0.06, bottom + height / 2, 0);
        leaf.box(M.door, 0.1, height - 0.2, 0.07, -side * 0.98, bottom + (height - 0.2) / 2, 0);
        leaf.box(M.door, 1.0, 0.12, 0.07, cx, bottom + 0.06, 0);
        leaf.box(M.door, 1.0, 0.14, 0.08, cx, bottom + height - 0.29, 0);
        const arch = new THREE.Shape();
        arch.absellipse(0, 0, 0.5, 0.22, 0, Math.PI, false);
        leaf.add(M.door, new THREE.ExtrudeGeometry(arch, { depth: 0.07, bevelEnabled: false, curveSegments: 12 }), cx, bottom + height - 0.22, -0.035);
        for (let i = 0; i < 6; i++) leaf.box(M.trim, 0.84, 0.1, 0.025, cx, bottom + 0.22 + i * 0.16, 0, -0.55);
        leaf.build(pivot);
        root.add(pivot);
        doors.push({ pivot, side });
    }

    // Światło wnętrza (rośnie przy wejściu) i snop światła z drzwi na ganek — prowadzi wzrok do wejścia.
    const interior = new THREE.PointLight("#ffb35c", 1, 17, 1.5);
    interior.position.set(0, 3.0, -4.2);
    const bar = new THREE.PointLight("#ffc27a", 1, 10, 1.5);
    bar.position.set(0, 2.6, -8.2);
    const spot = new THREE.SpotLight("#ffc27a", 1, 24, 0.5, 0.8, 1.3);
    spot.position.set(0, 2.9, -2.2);
    spot.target.position.set(0, 0, 7);
    root.add(interior, bar, spot, spot.target);

    const lanterns = [-3.3, 3.3].map(x => {
        const light = new THREE.PointLight("#ffc36e", 0, 11, 1.6);
        light.position.set(x, 3.15, 3.7);
        root.add(light);
        return light;
    });

    return { doors, interior, bar, spot, lanterns };
}

// Budynek przy ulicy w układzie lokalnym: front w z = 0 (w stronę ulicy), głębokość w -z, szerokość wzdłuż x.
function buildHouse(root, b, M, font, spec) {
    const { w, type, paint, text } = spec;
    const D = 7, h = type === "twoStory" ? 6.3 : type === "brick" ? 4.6 : type === "gable" ? 3.3 : 3.7;
    b.box(type === "brick" ? M.brick : paint, w, h, D, 0, h / 2, -D / 2);
    b.box(M.porch, w, 0.3, 1.8, 0, 0.15, 0.9);

    const lower = w >= 5 ? [-w / 4 - 0.2, w / 4 + 0.2] : [w / 4];
    const doorX = w >= 5 ? 0 : -w / 4;
    for (const x of lower) windowWithFrame(b, M.glass, M.townTrim, x, 1.9, 0.02, Math.min(1.4, w / 4), 1.5);
    b.box(M.townTrim, 1.2, 2.45, 0.1, doorX, 1.43, 0.03);
    b.box(M.door, 0.95, 2.25, 0.06, doorX, 1.43, 0.06);
    const awningPosts = () => { for (const x of [-w / 2 + 0.15, w / 2 - 0.15]) b.box(M.townTrim, 0.16, 2.9, 0.16, x, 1.6, 1.75); };

    if (type === "falseFront") {
        b.box(paint, w, 1.9, 0.2, 0, h + 0.95, -0.1);
        b.box(M.townTrim, w + 0.3, 0.16, 0.32, 0, h + 1.9, -0.05);
        b.box(M.townTrim, w + 0.1, 0.14, 0.3, 0, h, 0);
        b.box(M.roof, w + 0.2, 0.08, 1.95, 0, 3.05, 0.95, 0.13);
        awningPosts();
    } else if (type === "twoStory") {
        b.box(M.porch, w, 0.22, 1.8, 0, 3.2, 0.9);
        b.box(M.townTrim, w, 0.1, 0.1, 0, 4.1, 1.75);
        for (let x = -w / 2 + 0.2; x <= w / 2 - 0.19; x += 0.4) b.box(M.townTrim, 0.05, 0.8, 0.05, x, 3.7, 1.75);
        for (const x of [-w / 2 + 0.15, w / 2 - 0.15]) b.box(M.townTrim, 0.16, 3.1, 0.16, x, 1.55, 1.75);
        for (const x of lower) windowWithFrame(b, M.glass, M.townTrim, x, 4.8, 0.02, Math.min(1.3, w / 4), 1.5);
        b.box(M.townTrim, w + 0.4, 0.25, 0.45, 0, h, 0.05);
    } else if (type === "gable") {
        const rise = 1.7;
        const shape = new THREE.Shape([new THREE.Vector2(-w / 2 - 0.1, 0), new THREE.Vector2(w / 2 + 0.1, 0), new THREE.Vector2(0, rise)]);
        b.add(paint, new THREE.ExtrudeGeometry(shape, { depth: D, bevelEnabled: false }), 0, h, -D);
        const slope = Math.hypot(w / 2 + 0.4, rise), angle = Math.atan2(rise, w / 2 + 0.4);
        for (const side of [-1, 1]) b.box(M.roof, slope, 0.12, D + 0.5, side * (w / 4 + 0.2), h + rise / 2 + 0.06, -D / 2 + 0.2, 0, 0, -side * angle);
        b.box(M.roof, w + 0.2, 0.08, 1.95, 0, 3.0, 0.95, 0.13);
        awningPosts();
    } else {
        b.box(M.townTrim, w + 0.4, 0.34, 0.5, 0, h, 0.05);
        for (const x of [-w / 2 + 0.2, w / 2 - 0.2]) b.box(M.brick, 0.4, h, 0.2, x, h / 2, 0.05);
        b.box(M.roof, w + 0.2, 0.08, 1.95, 0, 3.0, 0.95, 0.13);
        awningPosts();
    }

    if (text) {
        const y = type === "falseFront" ? h + 0.95 : type === "twoStory" ? h - 0.55 : h - 0.75;
        const sw = Math.min(w - 0.8, 1.2 + text.length * 0.42), sh = 0.75;
        const texture = signTexture(text, { width: 512, height: Math.round(512 * sh / sw), font, bg: "#e6d4ad", fg: "#3b2414", border: "#6a4a2c" });
        root.add(b.place(sign(texture, sw, sh), 0, y, 0.14));
    }
}

function buildTown(root, b, M, font) {
    const P = M.paints;
    const lots = {
        [-1]: [
            { w: 7, type: "twoStory", paint: P[4], text: "HOTEL", gap: 0.6 },
            { w: 6.4, type: "falseFront", paint: P[0], text: "GENERAL STORE", gap: 0.9 },
            { w: 5, type: "gable", paint: P[1], gap: 1.2 },
            { w: 4.6, type: "falseFront", paint: P[3], text: "BARBER", gap: 0.5 },
            { w: 5.6, type: "falseFront", paint: P[2], text: "SUPPLY", gap: 0.5 }
        ],
        [1]: [
            { w: 5.6, type: "falseFront", paint: P[2], text: "POST OFFICE", gap: 0.8 },
            { w: 6.4, type: "brick", paint: P[0], text: "BANK", gap: 0.7 },
            { w: 7, type: "twoStory", paint: P[1], text: "BOARDING", gap: 1.1 },
            { w: 5, type: "gable", paint: P[3], gap: 0.6 },
            { w: 5.2, type: "falseFront", paint: P[4], text: "LIVERY", gap: 0.6 }
        ]
    };
    for (const side of [-1, 1]) {
        // Za linią saloonu (po bokach) — wypełnia szczeliny między fasadą a pierzejami.
        const specs = [{ z: -6, w: 10, type: "twoStory", paint: P[side < 0 ? 0 : 4] }];
        let z = 2.4;
        for (const spec of lots[side]) {
            specs.push({ ...spec, z: z + spec.w / 2 });
            z += spec.w + spec.gap;
        }
        for (const spec of specs) {
            b.frame.makeRotationY(side < 0 ? Math.PI / 2 : -Math.PI / 2).setPosition(side * 8.6, 0, spec.z);
            buildHouse(root, b, M, font, spec);
        }
    }
    b.frame.identity();

    // Pierwszy plan i ulica: poręcz dla koni, koryto, beczki, skrzynie.
    b.box(M.townTrim, 0.16, 1.1, 0.16, -5.7, 0.55, 6.2);
    b.box(M.townTrim, 0.16, 1.1, 0.16, -5.7, 0.55, 9.2);
    b.box(M.townTrim, 0.12, 0.12, 3.4, -5.7, 1.02, 7.7);
    b.box(M.board, 0.7, 0.5, 2.2, -5.3, 0.25, 11.2);
    for (const [x, z] of [[5.7, 5.3], [6.15, 6.1], [5.55, 6.6]]) {
        b.add(M.door, new THREE.CylinderGeometry(0.36, 0.33, 0.95, 12), x, 0.475, z);
        b.add(M.metal, new THREE.CylinderGeometry(0.375, 0.375, 0.06, 12), x, 0.72, z);
        b.add(M.metal, new THREE.CylinderGeometry(0.36, 0.36, 0.06, 12), x, 0.22, z);
    }
    b.box(M.board, 0.8, 0.7, 0.8, 5.9, 0.35, 9.4, 0, 0.3);
    b.box(M.board, 0.6, 0.55, 0.6, 5.85, 0.97, 9.35, 0, -0.2);

    // Wieża ciśnień i odległe mesy (mgła rozmywa je w horyzont).
    for (const [dx, dz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) b.box(M.townTrim, 0.22, 6.4, 0.22, -15 + dx * 1.1, 3.2, -15 + dz * 1.1);
    b.add(M.board, new THREE.CylinderGeometry(1.7, 1.7, 2.3, 16), -15, 7.5, -15);
    b.add(M.roof, new THREE.ConeGeometry(1.95, 1.1, 16), -15, 9.2, -15);
    for (const [x, z, rt, rb, h] of [[-70, -120, 14, 20, 16], [-28, -150, 9, 16, 22], [30, -135, 18, 24, 13], [85, -110, 10, 15, 19], [140, -160, 22, 30, 15], [-130, -150, 16, 24, 12]]) {
        b.add(M.mesa, new THREE.CylinderGeometry(rt, rb, h, 7), x, h / 2, z);
    }
}

function buildSky(root) {
    const geometry = new THREE.SphereGeometry(320, 32, 16);
    geometry.setAttribute("color", new THREE.BufferAttribute(new Float32Array(geometry.attributes.position.count * 3), 3));
    const dome = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.BackSide, fog: false, depthWrite: false }));
    dome.renderOrder = -1;

    const rnd = random(99), stars = [];
    for (let i = 0; i < 420; i++) {
        const a = rnd() * Math.PI * 2, e = 0.08 + Math.pow(rnd(), 0.7) * 1.3;
        stars.push(Math.cos(a) * Math.cos(e) * 300, Math.sin(e) * 300, Math.sin(a) * Math.cos(e) * 300);
    }
    const starGeometry = new THREE.BufferGeometry();
    starGeometry.setAttribute("position", new THREE.Float32BufferAttribute(stars, 3));
    const starField = new THREE.Points(starGeometry, new THREE.PointsMaterial({ color: "#fff4dc", size: 1.6, sizeAttenuation: false, fog: false, transparent: true, opacity: 0.85 }));
    const moon = new THREE.Mesh(new THREE.CircleGeometry(8, 32), new THREE.MeshBasicMaterial({ color: "#f3ecd6", fog: false }));
    moon.position.set(75, 105, -250);
    moon.lookAt(0, 0, 0);
    root.add(dome, starField, moon);
    return { dome, starField, moon };
}

function paintSky(dome, colors) {
    const top = new THREE.Color(colors.skyTop), mid = new THREE.Color(colors.skyMid), horizon = new THREE.Color(colors.skyHorizon), c = new THREE.Color();
    const p = dome.geometry.attributes.position, col = dome.geometry.attributes.color;
    for (let i = 0; i < p.count; i++) {
        // Horyzont → pas zachodu (do ~7°) → zenit.
        const e = clamp01(p.getY(i) / 320);
        if (e < 0.12) c.copy(horizon).lerp(mid, e / 0.12); else c.copy(mid).lerp(top, Math.pow(phase(e, 0.12, 0.7), 0.8));
        col.setXYZ(i, c.r, c.g, c.b);
    }
    col.needsUpdate = true;
}

function buildScene(font) {
    const scene = new THREE.Scene();
    const M = materials();
    const b = new Batch();

    const ground = new THREE.Mesh(new THREE.PlaneGeometry(700, 700), M.sand);
    ground.rotation.x = -Math.PI / 2;
    ground.updateMatrix();
    ground.geometry.applyMatrix4(ground.matrix);
    ground.rotation.x = 0;
    worldUv(ground.geometry, 10);
    ground.receiveShadow = true;
    const streetTexture = noiseTexture(2, 176, 900, true);
    streetTexture.repeat.set(1, 8);
    const street = new THREE.Mesh(new THREE.PlaneGeometry(13.6, 70), new THREE.MeshStandardMaterial({ color: "#c49563", map: streetTexture, roughness: 0.95 }));
    street.rotation.x = -Math.PI / 2;
    street.position.set(0, 0.01, 31);
    street.receiveShadow = true;
    scene.add(ground, street);

    const saloon = buildSaloon(scene, b, M, font);
    buildTown(scene, b, M, font);
    b.build(scene);

    const hemi = new THREE.HemisphereLight("#fff", "#000", 1);
    const sun = new THREE.DirectionalLight("#fff", 1);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    Object.assign(sun.shadow.camera, { left: -28, right: 28, top: 30, bottom: -24, near: 1, far: 140 });
    sun.shadow.bias = -0.0004;
    sun.shadow.normalBias = 0.03;
    sun.target.position.set(0, 0, 8);
    scene.add(hemi, sun, sun.target);
    scene.fog = new THREE.Fog("#fff", 40, 200);

    return { scene, M, hemi, sun, ...saloon, ...buildSky(scene) };
}

// ---- Kadr, tabliczki, motyw ---------------------------------------------------------------------------------------

// Kadr zależny od proporcji: szeroko ("porch") — saloon ~60% szerokości, linia ganku nisko, tabliczki na tablicach przy
// drzwiach; wąsko/pionowo ("stack") — saloon wyżej i większy, tabliczki HTML pod nim, bez nachodzenia na budynek.
function frameCamera(s) {
    const w = s.host.clientWidth || 1, h = s.host.clientHeight || 1, aspect = w / h;
    s.camera.aspect = aspect;
    let layout = aspect < 0.95 ? "stack" : "porch";
    pose(s, aspect, layout);
    if (layout === "porch" && plaqueWidth(s, w, h) < 140) {
        layout = "stack";
        pose(s, aspect, layout);
    }
    s.layout = layout;
    s.debug.layout = layout;
    s.debug.porchY = Math.round(toScreen(s, 0, 0, 4.3, w, h)[1]);   // dolna krawędź stopni ganku (px) — testy układu
    s.debug.saloonTopY = Math.round(toScreen(s, 0, SALOON_TOP, 0, w, h)[1]);
}

function pose(s, aspect, layout) {
    const stack = layout === "stack";
    const fill = stack ? (aspect < 0.62 ? 1.08 : 1.0) : Math.min(0.8, Math.max(0.56, 0.62 + (1.6 - aspect) * 0.4));
    const groundAt = stack ? stackGround(s) : 0.86;
    const tanV = Math.tan(THREE.MathUtils.degToRad(s.camera.fov / 2));
    const eye = 2.3;
    let d = (SALOON_W / 2) / (fill * tanV * aspect);
    let pitch = 0;
    for (let i = 0; i < 12; i++) {
        pitch = Math.atan(-eye / (d - 3.4)) - Math.atan((1 - 2 * groundAt) * tanV);
        const top = Math.tan(Math.atan((SALOON_TOP - eye) / d) - pitch) / tanV;
        if (top <= (stack ? 0.84 : 0.9)) break;
        d *= 1.05;
    }
    s.start.position.set(0, eye, d);
    s.start.target.set(0, eye + Math.tan(pitch) * d, 0);
    s.camera.position.copy(s.start.position);
    s.camera.lookAt(s.start.target);
    s.camera.updateProjectionMatrix();
    s.camera.updateMatrixWorld();
}

// "stack": linia ganku tuż nad blokiem tabliczek (tekst + trzy tabliczki na drogowskazie) — bez pustej ulicy między nimi.
// Przed włączeniem .has-3d blok ma jeszcze układ CSS, więc wysokość jest szacowana; obserwator poprawia kadr po zmianie.
function stackGround(s) {
    const h = s.host.clientHeight || 1;
    const block = s.signs && s.signs.closest(".has-3d") ? s.signs.getBoundingClientRect().height : 300;
    return Math.min(0.8, Math.max(0.5, 1 - (block + 20) / h));
}

function toScreen(s, x, y, z, w, h) {
    const v = new THREE.Vector3(x, y, z).project(s.camera);
    return [(v.x + 1) / 2 * w, (1 - v.y) / 2 * h];
}

function plaqueWidth(s, w, h) {
    const [x, y, z] = ANCHORS.guest;
    return toScreen(s, x + PLAQUE_W / 2, y, z, w, h)[0] - toScreen(s, x - PLAQUE_W / 2, y, z, w, h)[0];
}

// Położenie tabliczek HTML: projekcja punktów tablic ogłoszeń → zmienne CSS na kontenerze tabliczek.
function placeSigns(s) {
    const el = s.signs;
    if (!el) return;
    const w = s.host.clientWidth || 1, h = s.host.clientHeight || 1;
    el.dataset.signLayout = s.layout;
    if (s.layout !== "porch") return;
    for (const [name, [x, y, z]] of Object.entries(ANCHORS)) {
        const [px, py] = toScreen(s, x, y, z, w, h);
        el.style.setProperty(`--${name}-x`, `${px.toFixed(1)}px`);
        el.style.setProperty(`--${name}-y`, `${py.toFixed(1)}px`);
    }
    const unit = toScreen(s, 0, 1, 0.14, w, h)[1] - toScreen(s, 0, 2, 0.14, w, h)[1];
    el.style.setProperty("--sign-w", `${plaqueWidth(s, w, h).toFixed(1)}px`);
    el.style.setProperty("--sign-h", `${Math.max(40, unit * 0.78).toFixed(1)}px`);
}

function clearSigns(el) {
    if (!el) return;
    delete el.dataset.signLayout;
    for (const name of Object.keys(ANCHORS)) {
        el.style.removeProperty(`--${name}-x`);
        el.style.removeProperty(`--${name}-y`);
    }
    el.style.removeProperty("--sign-w");
    el.style.removeProperty("--sign-h");
}

function applyTheme(s, colors) {
    paintSky(s.dome, colors);
    s.scene.fog.color.set(colors.fog);
    s.scene.fog.near = colors.fogNear;
    s.scene.fog.far = colors.fogFar;
    s.hemi.color.set(colors.hemiSky);
    s.hemi.groundColor.set(colors.hemiGround);
    s.hemi.intensity = colors.hemi;
    s.sun.color.set(colors.sun);
    s.sun.intensity = colors.sunIntensity;
    s.sun.position.set(...colors.sunPosition);
    s.M.glass.color.set(colors.glass);
    s.M.glass.emissiveIntensity = colors.glassGlow;
    s.M.saloonGlass.emissiveIntensity = colors.saloonGlow;
    s.M.bulb.emissiveIntensity = colors.lanternGlow;
    s.scene.traverse(obj => {
        const kind = obj.material?.userData?.sign;
        if (kind) obj.material.emissiveIntensity = kind === "main" ? colors.signGlow : colors.townSignGlow;
    });
    for (const light of s.lanterns) light.intensity = colors.lantern;
    s.starField.visible = colors.stars;
    s.moon.visible = colors.stars;
    s.base = { interior: colors.interior, spot: colors.spot };
    s.renderer.toneMappingExposure = colors.exposure;
    s.renderer.shadowMap.needsUpdate = true;
    s.dirty = true;
}


// ---- Cykl życia ---------------------------------------------------------------------------------------------------

// Wynik: { renderer: "3d" | "css", reason } — dopiero gdy wiadomo, co pokazać. Przy "3d" pierwsza klatka sceny jest już
// narysowana w canvasie (pod warstwą "Pending"), więc Entrance może od razu przejść z Pending do 3D bez pustego kadru.
// Przy "css" w DOM nic nie zostaje (Entrance pokazuje scenę CSS/SVG — świadomy fallback).
// options: { force (?scene=3d), signs (kontener tabliczek), known3d (przeglądarka już wcześniej wybrała 3D — bez próby) }.
export async function init(host, options) {
    const force = !!options?.force;
    dispose();
    const token = ++generation;
    const result = await startScene({
        force,
        known3d: !!options?.known3d,
        build: () => { setup(host, options?.signs, force); return state; },
        isCurrent: () => token === generation,
        fail: dispose
    });
    window.__sansPost3dTimings = result.timings;
    return { renderer: result.renderer, reason: result.reason };
}

function setup(host, signs, force) {
    const renderer = createRenderer(host, force);
    try {
        const width = host.clientWidth || 1, height = host.clientHeight || 1;
        renderer.domElement.className = "entrance-3d-canvas";
        host.appendChild(renderer.domElement);

        const font = getComputedStyle(document.getElementById("entrance-title") || document.body).fontFamily || "serif";
        const world = buildScene(font);
        const camera = new THREE.PerspectiveCamera(40, width / height, 0.1, 700);

        state = {
            host, signs, renderer, camera, ...world,
            start: { position: new THREE.Vector3(), target: new THREE.Vector3() },
            end: { position: new THREE.Vector3(...END_POSITION), target: new THREE.Vector3(...END_TARGET) },
            target: new THREE.Vector3(),
            entering: null, dirty: true, layout: "porch",
            debug: { frames: 0, doorAngle: 0, cameraZ: 0, disposed: false, layout: "porch" }
        };
        window.__sansPost3d = state.debug;
        applyTheme(state, PALETTE[theme()]);
        frameCamera(state);
        placeSigns(state);
        state.debug.cameraZ = camera.position.z;

        state.resize = new ResizeObserver(() => {
            const s = state;
            if (!s) return;
            const w = host.clientWidth || 1, h = host.clientHeight || 1;
            s.renderer.setSize(w, h, false);
            if (s.entering) {
                s.camera.aspect = w / h;
                s.camera.updateProjectionMatrix();
            } else {
                frameCamera(s);
                placeSigns(s);
            }
            s.dirty = true;
        });
        state.resize.observe(host);
        if (signs) state.resize.observe(signs);

        // Zmiana motywu (przycisk w rogu) → niebo, mgła, światła, okna.
        state.themeObserver = new MutationObserver(() => state && applyTheme(state, PALETTE[theme()]));
        state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

        renderer.setAnimationLoop(tick);
    } catch (e) {
        if (!state) disposeScene(new THREE.Scene(), renderer);
        throw e;
    }
}

function tick(time) {
    const s = state;
    if (!s) return;
    if (!s.dirty && !s.entering) return;
    s.dirty = false;
    s.debug.frames++;

    let glow = 0, open = 0, dolly = 0;
    if (s.entering) {
        const t = clamp01((time - s.entering.start) / s.entering.duration);
        glow = easeOut(phase(t, 0, 0.5));                    // światło wnętrza rośnie
        open = easeOutBack(phase(t, 0.08, 0.46));            // drzwi otwierają się (lekkie odbicie)
        dolly = easeInOutCubic(phase(t, 0.14, 1));           // kamera płynnie przez próg
        if (t >= 1 && !s.entering.done) {
            s.entering.done = true;
            s.entering.resolve();
        }
    }

    s.interior.intensity = s.base.interior * (1 + 2.6 * glow);
    s.bar.intensity = s.base.interior * 0.6 * (1 + 2 * glow);
    s.spot.intensity = s.base.spot * (1 + 3 * glow);
    const angle = DOOR_OPEN * open;
    for (const d of s.doors) d.pivot.rotation.y = -d.side * angle;   // przeciwne kierunki, do środka
    s.debug.doorAngle = angle * 180 / Math.PI;

    s.camera.position.lerpVectors(s.start.position, s.end.position, dolly);
    s.target.lerpVectors(s.start.target, s.end.target, dolly);
    s.camera.lookAt(s.target);
    s.debug.cameraZ = s.camera.position.z;

    s.renderer.render(s.scene, s.camera);
}

// Wejście: światło, drzwi, kamera przez próg; Promise kończy się po animacji (Entrance nawiguje do /saloon).
// Znacznik "sp-through-door" mówi Main Hall, że gość właśnie przeszedł przez drzwi (krótki "settle" kamery).
export function enter(durationMs) {
    const s = state;
    if (!s) return Promise.resolve();
    if (s.entering) return s.entering.promise;
    let resolve;
    const promise = new Promise(r => { resolve = r; });
    s.entering = { start: performance.now(), duration: Math.max(1, durationMs), resolve, promise, done: false };
    try { sessionStorage.setItem("sp-through-door", String(Date.now())); } catch { /* bez storage — sala bez "settle" */ }
    return promise;
}

// Opuszczenie "/": pętla, obserwatory, geometrie, materiały, tekstury, kontekst WebGL.
export function dispose() {
    generation++;
    const s = state;
    if (!s) return;
    state = null;
    s.resize?.disconnect();
    s.themeObserver?.disconnect();
    s.entering?.resolve?.();
    disposeScene(s.scene, s.renderer);
    clearSigns(s.signs);
    s.debug.disposed = true;
}
