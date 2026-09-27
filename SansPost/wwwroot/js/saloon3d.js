// SansPost — Saloon Main Hall 3D (Sprint 15, dopracowanie wizualne 15B): wnętrze Saloonu dla /saloon na wydajnym GPU.
// Start, decyzja renderera, lazy Three.js, próba płynności i sprzątanie — wspólne z Entrance (js/scene3d-common.js);
// przy fallbacku zostaje klasyczny widok /saloon (Blazor).
// Cztery strefy: BAR (rozmowy — otwiera istniejący feed w panelu HTML), WANTED, GAME, MUSIC (na razie "wkrótce").
// Canvas jest obrazem sali (aria-hidden); każda akcja ma odpowiednik w HTML (przyciski stref w SaloonHall.razor) —
// na szerokim ekranie przyciski leżą dokładnie na szyldach stref w scenie, na wąskim stoją w pasku u dołu.
// Styl: stylizowane low-poly z dopracowanym światłem i materiałami (proceduralne tekstury z canvas, bez modeli GLB).
// Kamera: stały kadr główny, krótki "settle" po wejściu przez drzwi Entrance, kontrolowane przejazdy do stref.
// Bez FPS/WASD, bez reakcji kamery na kursor. Klatka rysowana tylko przy zmianie (ruch kamery, hover, motyw, resize).
// API (JS interop): init(host, options) → { renderer, reason }, focusArea(zone) → Promise, resetView() → Promise, dispose().

import {
    THREE, startScene, theme, reducedMotion, easeInOutCubic, createRenderer, disposeScene, canvasTexture,
    noiseTexture, headingFont, Batch, mergeGeometries, worldUv, random
} from "./scene3d-common.js";

let state = null;
let generation = 0;

// ---- Układ sali ---------------------------------------------------------------------------------------------------
// Wejście w z = 0 (za kamerą), bar pod tylną ścianą (z = -12.5), Wanted na lewej ścianie, kącik muzyczny w prawym
// tylnym rogu, stół gry przed nim. Środkiem biegnie chodnik prowadzący wzrok do baru.

const HALF_W = 6.5, DEPTH = 12.5, HEIGHT = 4.4;

// sign: szyld strefy w scenie (środek, szerokość, wysokość, kierunek frontu) — klikalny na canvas jak sama strefa.
// hit: niewidzialna bryła trafień (raycasting); view: kadr kamery po wybraniu strefy.
const ZONES = {
    bar: {
        sign: { center: [0, 3.55, -12.1], w: 2.7, h: 0.62, facing: "z" },
        hit: [[7.4, 3.3, 3.4], [0, 1.65, -10.9]],
        // Bar w lewej części kadru — na desktopie prawą stronę zajmie panel z feedem.
        view: { position: [-0.4, 2.1, -6.0], target: [1.9, 1.85, -11.6] }
    },
    wanted: {
        sign: { center: [-6.29, 2.15, -9.5], w: 3.4, h: 2.2, facing: "+x" },
        hit: [[1.0, 3.0, 4.0], [-6.0, 2.2, -9.5]],
        // Niemal na wprost tablicy, cała tablica w kadrze; kamera obrócona w stronę tylnej ściany, więc tablica leży w lewej
        // części ekranu — prawą stronę na desktopie zajmuje okno Wanted (Sprint 17), a tablica 3D zostaje widoczna obok.
        // Nisko, żeby lampy baru zostały nad kadrem.
        view: { position: [0.3, 2.1, -9.0], target: [-5.22, 2.15, -11.34] },
        portrait: { position: [-0.5, 2.1, -9.5], target: [-6.3, 2.15, -9.5] }
    },
    game: {
        sign: { center: [2.5, 3.4, -6.75], w: 1.1, h: 0.32, facing: "z" },
        // Tylko stół z krzesłami (do wysokości oparć) — nad nim widać kącik muzyczny, który musi dać się kliknąć.
        hit: [[3.0, 1.4, 2.6], [2.5, 0.7, -5.9]],
        // Z góry i z przodu: cały blat z kartami, oba krzesła i lampa nad stołem.
        view: { position: [2.5, 2.8, -3.1], target: [2.5, 0.8, -6.0] },
        portrait: { position: [2.5, 3.9, -2.0], target: [2.5, 0.8, -5.9] }
    },
    music: {
        sign: { center: [6.36, 3.35, -8.8], w: 1.4, h: 0.42, facing: "-x" },
        hit: [[3.0, 3.0, 4.4], [5.1, 1.4, -8.9]],
        // Z ukosa od strony sali: front pianina, stołek, gramofon i gitara na ścianie.
        view: { position: [3.0, 1.95, -6.9], target: [6.0, 1.3, -9.3] },
        portrait: { position: [2.0, 2.0, -9.2], target: [6.0, 1.2, -9.2] }
    }
};

// Kadr progu (jak koniec przejazdu przez drzwi na Entrance) — start "settle" po wejściu z Entrance.
const DOOR_VIEW = { position: [0, 1.75, -0.25], target: [0, 1.62, -12] };

function fovFor(aspect, horizontalDeg) {
    const v = 2 * Math.atan(Math.tan(THREE.MathUtils.degToRad(horizontalDeg / 2)) / aspect);
    return Math.min(70, Math.max(40, THREE.MathUtils.radToDeg(v)));
}

// Kadr główny: z wysokości oczu stojącego przy wejściu, chodnik prowadzi do baru; pionowo — kamera bliżej baru.
function mainView(aspect) {
    if (aspect >= 1.15) return { position: [0, 2.55, -0.55], target: [0, 1.55, -9.4], fov: fovFor(aspect, 82) };
    if (aspect >= 0.8) return { position: [0, 2.7, -0.45], target: [0, 1.45, -9.0], fov: fovFor(aspect, 80) };
    return { position: [0, 2.45, -2.9], target: [0.2, 1.6, -10.6], fov: 64 };
}

// Telefon (pionowo, układ mobilny): dolną część ekranu zajmują zapowiedź strefy i pasek stref.
const phoneLayout = () => !!window.matchMedia?.("(max-width: 767.98px)").matches;

// Kamera pochylona o "deg" w dół przy tej samej pozycji: cel (target) leży wtedy o tyle stopni nad środkiem kadru.
function tiltDown(position, target, deg) {
    const [dx, dy, dz] = target.map((v, i) => v - position[i]);
    const length = Math.hypot(dx, dy, dz), flat = Math.hypot(dx, dz);
    const pitch = Math.atan2(dy, flat) - THREE.MathUtils.degToRad(deg);
    return [position[0] + dx / flat * Math.cos(pitch) * length, position[1] + Math.sin(pitch) * length, position[2] + dz / flat * Math.cos(pitch) * length];
}

// Widok strefy; na wąskim ekranie kamera cofa się wzdłuż osi widoku (w granicach sali), żeby strefa zmieściła się w kadrze.
// Pionowo strefa ma własny kadr (portrait: bliżej, bez lamp baru i żyrandola na pierwszym planie). Na telefonie kamera
// jest dodatkowo pochylona, więc strefa leży w górnej części ekranu — nad zapowiedzią i paskiem stref.
function zoneView(zone, aspect) {
    const portrait = ZONES[zone].portrait;
    if (portrait && aspect < 0.8) {
        const target = phoneLayout() ? tiltDown(portrait.position, portrait.target, 16) : portrait.target;
        return { position: portrait.position, target, fov: 64 };
    }
    const { position, target } = ZONES[zone].view;
    const k = aspect < 0.8 ? 1.45 : aspect < 1.15 ? 1.15 : 1;
    const p = position.map((v, i) => target[i] + (v - target[i]) * k);
    return {
        position: [Math.max(-HALF_W + 0.4, Math.min(HALF_W - 0.4, p[0])), Math.min(HEIGHT - 0.35, p[1]), Math.max(-DEPTH + 0.4, Math.min(-0.3, p[2]))],
        target,
        fov: aspect < 0.8 ? 64 : fovFor(aspect, 74)
    };
}

// Light: ciepłe światło dzienne od okien, bar i tak najjaśniejszy. Dark: lampy i szyldy, ciemniejsze boki i narożniki.
const PALETTE = {
    light: { hemiSky: "#fff1dc", hemiGround: "#8a6040", hemi: 3.4, sun: "#ffe0b0", sunIntensity: 7, sunPosition: [3, 4.6, 12],
             outside: "#f3d7ab", windowGlow: 2.2, windowColor: "#fff1d6", barKey: 24, bar: 2.4, chandelier: 1.4, accent: 3.2,
             bulbs: 1.1, signGlow: 0.14, mirrorGlow: 0.3, halo: 0.25, exposure: 1.3, wall: "#eadbd2", wood: "#ffffff" },
    dark:  { hemiSky: "#35304a", hemiGround: "#120a06", hemi: 0.34, sun: "#9fb2ff", sunIntensity: 0.25, sunPosition: [-5, 9, 12],
             outside: "#0c1020", windowGlow: 0.45, windowColor: "#26365f", barKey: 60, bar: 5.5, chandelier: 4.2, accent: 9,
             bulbs: 3.2, signGlow: 0.5, mirrorGlow: 0.45, halo: 0.85, exposure: 1.0, wall: "#c9b2aa", wood: "#e6dcd6" }
};

// ---- Tekstury proceduralne (kolorowe — lekkie zróżnicowanie tonów, bez ciężkiego szumu) ---------------------------------

function hsl(color) {
    const out = {};
    new THREE.Color(color).getHSL(out);
    return out;
}

// Deski: każda w nieco innym odcieniu i jasności, słoje, pojedyncze sęki, ciemniejsze fugi; podłoga z przesuniętymi łączeniami.
function woodTexture(seed, base, { boards = 6, horizontal = false, staggered = false, knots = 3, nails = false, size: px = 512 } = {}) {
    const rnd = random(seed), c = hsl(base);
    return canvasTexture(px, px, (ctx, w) => {
        const size = w / boards;
        const rect = (along, across, length, thickness) =>
            horizontal ? ctx.fillRect(along, across, length, thickness) : ctx.fillRect(across, along, thickness, length);
        const tone = (dl = 0) => {
            const h = (c.h + (rnd() - 0.5) * 0.025) * 360, s = Math.min(1, c.s * (0.85 + rnd() * 0.3)) * 100;
            const l = Math.min(0.9, Math.max(0.04, c.l * (0.82 + rnd() * 0.34) + dl)) * 100;
            return `hsl(${h.toFixed(1)}, ${s.toFixed(1)}%, ${l.toFixed(1)}%)`;
        };
        const plank = (i, from, length) => {
            ctx.fillStyle = tone();
            rect(from, i * size, length, size);
            // Słoje wzdłuż deski.
            for (let g = 0; g < 10; g++) {
                const offset = rnd() * size, dark = rnd() < 0.5;
                ctx.strokeStyle = dark ? `rgba(20,10,4,${0.06 + rnd() * 0.1})` : `rgba(255,220,170,${0.03 + rnd() * 0.04})`;
                ctx.lineWidth = 0.6 + rnd() * 1.2;
                ctx.beginPath();
                for (let p = 0; p <= 24; p++) {
                    const a = from + p / 24 * length;
                    const b = i * size + offset + Math.sin(p * 0.55 + g * 1.7) * (1.2 + rnd() * 0.6);
                    if (horizontal) ctx.lineTo(a, b); else ctx.lineTo(b, a);
                }
                ctx.stroke();
            }
            // Cieniowanie krawędzi deski (lekka wypukłość).
            ctx.fillStyle = "rgba(0,0,0,0.12)";
            rect(from, i * size + size - 4, length, 4);
            ctx.fillStyle = "rgba(255,230,190,0.06)";
            rect(from, i * size + 2, length, 3);
        };
        for (let i = 0; i < boards; i++) {
            if (staggered) {
                const joint = (0.2 + rnd() * 0.6) * w;
                plank(i, 0, joint);
                plank(i, joint, w - joint);
                ctx.fillStyle = "rgba(15,8,3,0.65)";
                rect(joint - 1, i * size, 2, size);
                if (nails) {
                    ctx.fillStyle = "rgba(30,24,20,0.8)";
                    for (const dx of [-7, 7]) for (const dy of [0.3, 0.7]) {
                        const x = joint + dx, y = i * size + size * dy;
                        ctx.beginPath(); ctx.arc(horizontal ? x : y, horizontal ? y : x, 1.6, 0, Math.PI * 2); ctx.fill();
                    }
                }
            } else {
                plank(i, 0, w);
            }
            ctx.fillStyle = "rgba(12,6,2,0.7)";
            rect(0, i * size, w, 2);
        }
        // Sęki.
        for (let k = 0; k < knots; k++) {
            const x = rnd() * w, y = rnd() * w, r = 3 + rnd() * 5;
            for (let ring = 3; ring >= 1; ring--) {
                ctx.fillStyle = `rgba(30,15,6,${0.12 * ring})`;
                ctx.beginPath(); ctx.ellipse(x, y, horizontal ? r * ring * 1.8 : r * ring * 0.7, horizontal ? r * ring * 0.7 : r * ring * 1.8, 0, 0, Math.PI * 2); ctx.fill();
            }
        }
    });
}

// Tapeta: głęboka czerwień w wąskie prążki, drobny złoty ornament (romb z kropkami) — spokojnie, bez dużych plam.
function damaskTexture() {
    return canvasTexture(256, 256, (ctx, w, h) => {
        const rnd = random(19);
        ctx.fillStyle = "#6a2219";
        ctx.fillRect(0, 0, w, h);
        for (let x = 0; x < w; x += 32) {
            ctx.fillStyle = "rgba(30,6,3,0.22)";
            ctx.fillRect(x, 0, 12, h);
            ctx.fillStyle = "rgba(212,160,90,0.10)";
            ctx.fillRect(x + 14, 0, 1.5, h);
        }
        for (let i = 0; i < 1200; i++) {
            ctx.fillStyle = `rgba(${rnd() < 0.5 ? "20,5,2" : "150,70,45"},${0.03 + rnd() * 0.04})`;
            ctx.fillRect(rnd() * w, rnd() * h, 2, 2);
        }
        for (let y = 16; y < h; y += 64) for (let x = 23; x < w; x += 64) {
            const ox = (y / 64) % 2 ? 32 : 0;
            ctx.save();
            ctx.translate((x + ox) % w, y);
            ctx.fillStyle = "rgba(214,164,96,0.16)";
            ctx.beginPath(); ctx.moveTo(0, -7); ctx.lineTo(5, 0); ctx.lineTo(0, 7); ctx.lineTo(-5, 0); ctx.closePath(); ctx.fill();
            for (const [dx, dy] of [[0, -11], [0, 11], [-9, 0], [9, 0]]) { ctx.beginPath(); ctx.arc(dx, dy, 1.3, 0, Math.PI * 2); ctx.fill(); }
            ctx.restore();
        }
    });
}

// Lustro barowe: ciepłe, przyciemnione odbicie sali — łuna trzech lamp, smugi i patyna przy krawędziach (bez prawdziwych odbić).
function mirrorTexture() {
    return canvasTexture(512, 320, (ctx, w, h) => {
        const rnd = random(23);
        const base = ctx.createLinearGradient(0, 0, 0, h);
        base.addColorStop(0, "#3a2818");
        base.addColorStop(0.55, "#2a1b10");
        base.addColorStop(1, "#1a110a");
        ctx.fillStyle = base;
        ctx.fillRect(0, 0, w, h);
        for (const x of [0.18, 0.5, 0.82]) {
            const glow = ctx.createRadialGradient(x * w, h * 0.34, 4, x * w, h * 0.34, w * 0.2);
            glow.addColorStop(0, "rgba(255,214,150,0.32)");
            glow.addColorStop(0.35, "rgba(214,140,70,0.12)");
            glow.addColorStop(1, "rgba(214,140,70,0)");
            ctx.fillStyle = glow;
            ctx.fillRect(0, 0, w, h);
        }
        // Odbite rzędy butelek (ciemne sylwetki) i blat.
        for (let i = 0; i < 26; i++) {
            const x = 12 + i * 19 + rnd() * 6, bh = 26 + rnd() * 22;
            ctx.fillStyle = `rgba(10,6,3,${0.35 + rnd() * 0.2})`;
            ctx.fillRect(x, h - 44 - bh, 9, bh);
            ctx.fillRect(x + 3, h - 52 - bh, 3, 10);
        }
        ctx.fillStyle = "rgba(8,4,2,0.55)";
        ctx.fillRect(0, h - 44, w, 44);
        // Ukośne smugi i patyna.
        ctx.strokeStyle = "rgba(255,235,200,0.025)";
        for (let i = 0; i < 4; i++) {
            ctx.lineWidth = 30 + rnd() * 40;
            ctx.beginPath(); ctx.moveTo(rnd() * w, 0); ctx.lineTo(rnd() * w - 120, h); ctx.stroke();
        }
        const edge = ctx.createRadialGradient(w / 2, h / 2, h * 0.4, w / 2, h / 2, w * 0.62);
        edge.addColorStop(0, "rgba(0,0,0,0)");
        edge.addColorStop(1, "rgba(10,6,2,0.65)");
        ctx.fillStyle = edge;
        ctx.fillRect(0, 0, w, h);
        for (let i = 0; i < 70; i++) {
            ctx.fillStyle = `rgba(60,45,30,${0.1 + rnd() * 0.2})`;
            const nearEdge = rnd() < 0.5;
            ctx.beginPath(); ctx.arc(nearEdge ? rnd() * w : (rnd() < 0.5 ? rnd() * 40 : w - rnd() * 40), nearEdge ? (rnd() < 0.5 ? rnd() * 30 : h - rnd() * 30) : rnd() * h, 1 + rnd() * 3, 0, Math.PI * 2); ctx.fill();
        }
    }, false);
}

// Chodnik w przejściu do baru: czerwień, złota bordiura, romby.
function runnerTexture() {
    return canvasTexture(256, 1024, (ctx, w, h) => {
        const rnd = random(41);
        ctx.fillStyle = "#6a1d16";
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = "#2a0d08";
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = "#7a2419";
        ctx.fillRect(10, 10, w - 20, h - 20);
        ctx.strokeStyle = "#c79a4e";
        ctx.lineWidth = 5;
        ctx.strokeRect(22, 22, w - 44, h - 44);
        ctx.lineWidth = 1.5;
        ctx.strokeRect(34, 34, w - 68, h - 68);
        for (let y = 120; y < h - 80; y += 160) {
            ctx.save();
            ctx.translate(w / 2, y);
            ctx.rotate(Math.PI / 4);
            ctx.fillStyle = "#3a120c";
            ctx.fillRect(-42, -42, 84, 84);
            ctx.strokeStyle = "#c79a4e";
            ctx.lineWidth = 3;
            ctx.strokeRect(-30, -30, 60, 60);
            ctx.fillStyle = "#b8843f";
            ctx.fillRect(-8, -8, 16, 16);
            ctx.restore();
        }
        for (let i = 0; i < 2500; i++) {
            ctx.fillStyle = `rgba(${rnd() < 0.5 ? "0,0,0" : "255,200,150"},${0.04 + rnd() * 0.05})`;
            ctx.fillRect(rnd() * w, rnd() * h, 2, 2);
        }
    }, false);
}

// Szyld strefy: ciemna deska ze słojami, złota podwójna rama, złocone litery z cieniem (styl ręcznie malowanego szyldu).
function plaqueTexture(text, font, { width = 768, height = 224, size = 0.5 } = {}) {
    return canvasTexture(width, height, (ctx, w, h) => {
        const rnd = random(text.length * 13);
        const wood = ctx.createLinearGradient(0, 0, 0, h);
        wood.addColorStop(0, "#2c1a0e");
        wood.addColorStop(1, "#1a0e07");
        ctx.fillStyle = wood;
        ctx.fillRect(0, 0, w, h);
        for (let g = 0; g < 26; g++) {
            ctx.strokeStyle = `rgba(0,0,0,${0.12 + rnd() * 0.14})`;
            ctx.lineWidth = 1 + rnd() * 2;
            const y = rnd() * h;
            ctx.beginPath(); ctx.moveTo(0, y); ctx.bezierCurveTo(w * 0.3, y + 6, w * 0.6, y - 6, w, y + 3); ctx.stroke();
        }
        const gold = ctx.createLinearGradient(0, 0, 0, h);
        gold.addColorStop(0, "#f7e0a3");
        gold.addColorStop(0.5, "#c4923f");
        gold.addColorStop(1, "#f0cf86");
        ctx.strokeStyle = gold;
        ctx.lineWidth = h * 0.05;
        ctx.strokeRect(h * 0.07, h * 0.07, w - h * 0.14, h - h * 0.14);
        ctx.lineWidth = 2;
        ctx.strokeRect(h * 0.15, h * 0.15, w - h * 0.3, h - h * 0.3);
        for (const [x, y] of [[h * 0.15, h * 0.15], [w - h * 0.15, h * 0.15], [h * 0.15, h - h * 0.15], [w - h * 0.15, h - h * 0.15]]) {
            ctx.save(); ctx.translate(x, y); ctx.rotate(Math.PI / 4); ctx.fillStyle = gold; ctx.fillRect(-5, -5, 10, 10); ctx.restore();
        }
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        let fontSize = h * size;
        ctx.letterSpacing = `${Math.round(h * 0.04)}px`;
        ctx.font = `700 ${Math.round(fontSize)}px ${font}`;
        while (ctx.measureText(text).width > w * 0.78 && fontSize > 12) ctx.font = `700 ${Math.round(fontSize -= 2)}px ${font}`;
        ctx.fillStyle = "rgba(0,0,0,0.6)";
        ctx.fillText(text, w / 2 + 3, h * 0.54 + 4);
        ctx.fillStyle = gold;
        ctx.fillText(text, w / 2, h * 0.54);
    }, false);
}

// Tabliczka kredowa "wkrótce" (zapowiedź strefy w świecie sali).
function chalkTexture(font) {
    return canvasTexture(256, 96, (ctx, w, h) => {
        ctx.fillStyle = "#5a3a22";
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = "#1f2622";
        ctx.fillRect(8, 8, w - 16, h - 16);
        const rnd = random(5);
        for (let i = 0; i < 300; i++) {
            ctx.fillStyle = `rgba(240,240,230,${rnd() * 0.05})`;
            ctx.fillRect(8 + rnd() * (w - 16), 8 + rnd() * (h - 16), 2, 1);
        }
        ctx.fillStyle = "rgba(240,238,226,0.92)";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.font = `italic 600 40px ${font}`;
        ctx.fillText("wkrótce", w / 2, h / 2 + 2);
    }, false);
}

// Tablica Wanted: jedna tekstura (canvas 1024×664) dla całej tablicy — nagłówek, główny list gończy "MOST WANTED"
// (przydomek i tytuł rozmowy z rankingu) i cztery mniejsze (#2–#5). Rysowana od razu z pustymi listami (sylwetka i "?"),
// potem setWanted przerysowuje ten sam canvas wynikiem z serwera. Pusta tablica: "Tablica czeka na pierwszą rozmowę."
// Bez sztucznych nazwisk — wolne miejsca to anonimowe, wyblakłe listy.
const WANTED_CANVAS = [1024, 664];
const WANTED_MAIN = { x: 272, y: 372, w: 372, h: 520, angle: -0.018 };
const WANTED_SMALL = [[606, 250], [846, 262], [606, 520], [846, 530]].map(([x, y], i) => ({ x, y, w: 212, h: 248, angle: [0.03, -0.025, -0.02, 0.028][i] }));
const WANTED_SERIF = "Georgia, 'Times New Roman', serif";

function wantedTexture(font) {
    const [width, height] = WANTED_CANVAS;
    const texture = canvasTexture(width, height, (ctx, w, h) => drawWanted(ctx, w, h, font, null), false);
    texture.userData.redraw = posters => {
        const canvas = texture.image;
        drawWanted(canvas.getContext("2d"), canvas.width, canvas.height, font, posters);
        texture.needsUpdate = true;
    };
    return texture;
}

// Tekst dopasowany do szerokości: mniejszy krój, a gdy i to za mało — skrót z "…".
function fitText(ctx, text, maxWidth, size, minSize, style, family) {
    let px = size;
    ctx.font = `${style} ${px}px ${family}`;
    while (px > minSize && ctx.measureText(text).width > maxWidth) {
        px -= 2;
        ctx.font = `${style} ${px}px ${family}`;
    }
    if (ctx.measureText(text).width <= maxWidth) return text;
    let cut = text;
    while (cut.length > 1 && ctx.measureText(cut + "…").width > maxWidth) cut = cut.slice(0, -1);
    return cut.trimEnd() + "…";
}

// Zawijanie słów do maxLines linii; linia za długa (jedno długie słowo) albo ostatnia przy uciętym tekście — skrót z "…".
function wrapText(ctx, text, maxWidth, maxLines) {
    const words = String(text).split(/\s+/).filter(Boolean);
    const lines = [];
    let line = "", used = 0;
    for (const word of words) {
        const next = line ? `${line} ${word}` : word;
        if (!line || ctx.measureText(next).width <= maxWidth) { line = next; used++; continue; }
        lines.push(line);
        line = "";
        if (lines.length === maxLines) break;
        line = word;
        used++;
    }
    if (line) lines.push(line);
    const clipped = used < words.length;
    return lines.map((text, i) => {
        if (ctx.measureText(text).width <= maxWidth && !(clipped && i === lines.length - 1)) return text;
        while (text.length > 1 && ctx.measureText(text + "…").width > maxWidth) text = text.slice(0, -1);
        return text.trimEnd() + "…";
    });
}

function drawWanted(ctx, w, h, font, posters) {
    const rnd = random(31);
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.letterSpacing = "0px";
    const board = ctx.createLinearGradient(0, 0, w, h);
    board.addColorStop(0, "#4a2d19");
    board.addColorStop(1, "#33200f");
    ctx.fillStyle = board;
    ctx.fillRect(0, 0, w, h);
    for (let g = 0; g < 70; g++) {
        ctx.strokeStyle = `rgba(0,0,0,${0.08 + rnd() * 0.12})`;
        ctx.lineWidth = 1 + rnd() * 2;
        const y = rnd() * h;
        ctx.beginPath(); ctx.moveTo(0, y); ctx.bezierCurveTo(w * 0.3, y + 4, w * 0.7, y - 4, w, y + (rnd() - 0.5) * 6); ctx.stroke();
    }
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = `700 76px ${font}`;
    ctx.letterSpacing = "16px";
    ctx.fillStyle = "rgba(0,0,0,0.5)";
    ctx.fillText("WANTED", w / 2 + 3, 62);
    ctx.fillStyle = "#f0d08a";
    ctx.fillText("WANTED", w / 2, 58);
    ctx.letterSpacing = "0px";

    const list = Array.isArray(posters) ? posters : null;
    const ink = "rgba(60,36,18,0.92)";

    // Kartka listu gończego: papier, cień, pinezka; rysunek treści w układzie środka kartki.
    const paper = ({ x, y, w: pw, h: ph, angle }, faded, content) => {
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(angle);
        ctx.shadowColor = "rgba(0,0,0,0.45)";
        ctx.shadowBlur = 12;
        ctx.shadowOffsetY = 6;
        const tone = ctx.createRadialGradient(0, 0, pw * 0.2, 0, 0, Math.max(pw, ph) * 0.75);
        tone.addColorStop(0, faded ? "#e4d4b2" : "#f3e4c2");
        tone.addColorStop(0.75, faded ? "#d2bd8e" : "#e3cb97");
        tone.addColorStop(1, faded ? "#a98458" : "#b98f58");
        ctx.fillStyle = tone;
        ctx.fillRect(-pw / 2, -ph / 2, pw, ph);
        ctx.shadowColor = "transparent";
        ctx.strokeStyle = "rgba(60,36,18,0.35)";
        ctx.lineWidth = 2;
        ctx.strokeRect(-pw / 2 + 8, -ph / 2 + 8, pw - 16, ph - 16);
        content(pw, ph);
        ctx.fillStyle = "#a3342a";
        ctx.beginPath(); ctx.arc(0, -ph / 2 + 9, 7, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = "rgba(255,255,255,0.5)";
        ctx.beginPath(); ctx.arc(-2, -ph / 2 + 7, 2.4, 0, Math.PI * 2); ctx.fill();
        ctx.restore();
    };

    // Szkic poszukiwanego: kapelusz, głowa, ramiona (węgiel), w ramce.
    const sketch = (cx, top, fw, fh, question) => {
        const fx = cx - fw / 2;
        ctx.strokeStyle = "rgba(60,36,18,0.6)";
        ctx.lineWidth = 2;
        ctx.strokeRect(fx, top, fw, fh);
        ctx.save();
        ctx.beginPath(); ctx.rect(fx, top, fw, fh); ctx.clip();
        const cy = top + fh * 0.6;
        ctx.fillStyle = "rgba(40,24,12,0.7)";
        ctx.beginPath(); ctx.ellipse(cx, cy + fh * 0.45, fw * 0.36, fh * 0.3, 0, Math.PI, 0); ctx.fill();
        ctx.beginPath(); ctx.ellipse(cx, cy, fh * 0.16, fh * 0.2, 0, 0, Math.PI * 2); ctx.fill();
        ctx.beginPath(); ctx.ellipse(cx, cy - fh * 0.16, fh * 0.34, fh * 0.05, 0, 0, Math.PI * 2); ctx.fill();
        ctx.fillRect(cx - fh * 0.15, cy - fh * 0.36, fh * 0.3, fh * 0.2);
        if (question) {
            ctx.fillStyle = "rgba(230,210,170,0.3)";
            ctx.font = `700 ${Math.round(fh * 0.4)}px ${font}`;
            ctx.fillText("?", cx, cy + fh * 0.04);
        }
        ctx.restore();
    };

    const rule = (y, half) => {
        ctx.fillStyle = "rgba(60,36,18,0.5)";
        ctx.fillRect(-half, y, half * 2, 2);
    };

    // Główny list gończy.
    const main = list?.[0];
    paper(WANTED_MAIN, !main, (pw, ph) => {
        const top = -ph / 2, inner = pw - 60;
        ctx.fillStyle = ink;
        ctx.letterSpacing = "3px";
        ctx.fillText(fitText(ctx, "MOST WANTED", inner, 44, 26, "700", font), 0, top + 52);
        ctx.letterSpacing = "0px";
        rule(top + 82, inner / 2);
        sketch(0, top + 98, 150, 118, !main);
        if (main) {
            ctx.fillStyle = "#2c180b";
            ctx.fillText(fitText(ctx, main.alias, inner, 52, 24, "700", font), 0, top + 262);
            ctx.fillStyle = "rgba(60,36,18,0.85)";
            ctx.fillText(fitText(ctx, "za rozmowę, która rozpaliła Saloon", inner, 22, 14, "italic 400", WANTED_SERIF), 0, top + 300);
            rule(top + 322, inner / 2 - 40);
            ctx.fillStyle = "#2c180b";
            ctx.font = `700 27px ${WANTED_SERIF}`;
            const lines = wrapText(ctx, `„${main.title}”`, inner, 4);
            lines.forEach((text, i) => ctx.fillText(text, 0, top + 360 + i * 34));
        } else {
            ctx.fillStyle = "rgba(60,36,18,0.8)";
            ctx.font = `italic 700 28px ${WANTED_SERIF}`;
            const message = list ? "Tablica czeka na pierwszą rozmowę." : "";
            wrapText(ctx, message, inner, 3).forEach((text, i) => ctx.fillText(text, 0, top + 280 + i * 36));
        }
    });

    // Pozostali poszukiwani (#2–#5); wolne miejsca — anonimowe, wyblakłe listy.
    WANTED_SMALL.forEach((slot, i) => {
        const poster = list?.[i + 1];
        paper(slot, !poster, (pw, ph) => {
            const top = -ph / 2, inner = pw - 36;
            ctx.fillStyle = ink;
            ctx.letterSpacing = "2px";
            ctx.fillText(fitText(ctx, "WANTED", pw - 96, 24, 16, "700", font), 0, top + 38);
            ctx.letterSpacing = "0px";
            sketch(0, top + 58, 98, 78, !poster);
            ctx.fillStyle = "#8f2d20";
            ctx.font = `700 19px ${font}`;
            ctx.fillText(`#${i + 2}`, -pw / 2 + 28, top + 30);
            if (poster) {
                ctx.fillStyle = "#2c180b";
                ctx.fillText(fitText(ctx, poster.alias, inner, 30, 16, "700", font), 0, top + 166);
                ctx.font = `700 17px ${WANTED_SERIF}`;
                wrapText(ctx, `„${poster.title}”`, inner, 2).forEach((text, l) => ctx.fillText(text, 0, top + 196 + l * 21));
            } else {
                ctx.fillStyle = "rgba(60,36,18,0.35)";
                for (let l = 0; l < 3; l++) ctx.fillRect(-inner / 2 + 10, top + 162 + l * 18, inner - 20 - l * 24, 2);
            }
        });
    });
    ctx.restore();
}

// Rekwizyty stołu pojedynku (jedna tekstura, jedno wywołanie rysowania): pięć kart akcji, rewers, znacznik rundy,
// notatka z zasadami i emblemat na suknie. Czysto wizualna zapowiedź gry "Śladem Rewolwerowca" — bez logiki.
const ATLAS = {
    cards: ["SHOOT", "DODGE", "RELOAD", "BLOCK", "TAUNT"].map((title, i) => ({ title, x: i * 204, y: 0, w: 200, h: 280 })),
    back: { x: 0, y: 292, w: 200, h: 280 },
    round: { x: 212, y: 292, w: 256, h: 256 },
    rules: { x: 480, y: 292, w: 360, h: 280 },
    emblem: { x: 0, y: 600, w: 400, h: 400 }
};

function propsTexture(font) {
    const subtitles = { SHOOT: "Strzał", DODGE: "Unik", RELOAD: "Przeładuj", BLOCK: "Blok", TAUNT: "Prowokacja" };
    return canvasTexture(1024, 1024, (ctx) => {
        const rounded = (x, y, w, h, r) => { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); };
        const icon = (title, cx, cy, s) => {
            ctx.strokeStyle = "#5a2a16";
            ctx.fillStyle = "#5a2a16";
            ctx.lineWidth = s * 0.08;
            ctx.lineCap = "round";
            if (title === "SHOOT") {            // celownik
                ctx.beginPath(); ctx.arc(cx, cy, s * 0.42, 0, Math.PI * 2); ctx.stroke();
                ctx.beginPath(); ctx.arc(cx, cy, s * 0.14, 0, Math.PI * 2); ctx.fill();
                for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) { ctx.beginPath(); ctx.moveTo(cx + dx * s * 0.28, cy + dy * s * 0.28); ctx.lineTo(cx + dx * s * 0.6, cy + dy * s * 0.6); ctx.stroke(); }
            } else if (title === "DODGE") {     // łuk uniku ze strzałką
                ctx.beginPath(); ctx.arc(cx, cy + s * 0.1, s * 0.42, Math.PI * 1.1, Math.PI * 1.9); ctx.stroke();
                ctx.beginPath(); ctx.moveTo(cx + s * 0.42, cy - s * 0.12); ctx.lineTo(cx + s * 0.18, cy - s * 0.2); ctx.lineTo(cx + s * 0.36, cy + s * 0.08); ctx.fill();
                ctx.beginPath(); ctx.arc(cx - s * 0.1, cy + s * 0.3, s * 0.12, 0, Math.PI * 2); ctx.fill();
            } else if (title === "RELOAD") {    // bęben rewolweru
                ctx.beginPath(); ctx.arc(cx, cy, s * 0.46, 0, Math.PI * 2); ctx.stroke();
                for (let i = 0; i < 6; i++) { const a = i / 6 * Math.PI * 2; ctx.beginPath(); ctx.arc(cx + Math.cos(a) * s * 0.26, cy + Math.sin(a) * s * 0.26, s * 0.09, 0, Math.PI * 2); ctx.fill(); }
            } else if (title === "BLOCK") {     // tarcza
                ctx.beginPath(); ctx.moveTo(cx, cy - s * 0.5); ctx.lineTo(cx + s * 0.42, cy - s * 0.32); ctx.quadraticCurveTo(cx + s * 0.4, cy + s * 0.3, cx, cy + s * 0.52);
                ctx.quadraticCurveTo(cx - s * 0.4, cy + s * 0.3, cx - s * 0.42, cy - s * 0.32); ctx.closePath(); ctx.stroke();
                ctx.beginPath(); ctx.moveTo(cx, cy - s * 0.3); ctx.lineTo(cx, cy + s * 0.3); ctx.stroke();
            } else {                            // prowokacja: dymek z "!"
                rounded(cx - s * 0.5, cy - s * 0.4, s, s * 0.66, s * 0.14); ctx.stroke();
                ctx.beginPath(); ctx.moveTo(cx - s * 0.2, cy + s * 0.26); ctx.lineTo(cx - s * 0.34, cy + s * 0.5); ctx.lineTo(cx - s * 0.02, cy + s * 0.26); ctx.fill();
                ctx.font = `700 ${Math.round(s * 0.5)}px ${font}`; ctx.textAlign = "center"; ctx.textBaseline = "middle"; ctx.fillText("!", cx, cy - s * 0.06);
            }
        };
        // Karty akcji: kremowy papier, podwójna ramka, tytuł, ikona, podpis po polsku.
        for (const card of ATLAS.cards) {
            const { x, y, w, h, title } = card;
            ctx.fillStyle = "#3a2213";
            rounded(x, y, w, h, 14); ctx.fill();
            const paper = ctx.createLinearGradient(x, y, x, y + h);
            paper.addColorStop(0, "#f3e6c6");
            paper.addColorStop(1, "#e2cf9f");
            ctx.fillStyle = paper;
            rounded(x + 7, y + 7, w - 14, h - 14, 10); ctx.fill();
            ctx.strokeStyle = "#9a6a32"; ctx.lineWidth = 2;
            rounded(x + 14, y + 14, w - 28, h - 28, 8); ctx.stroke();
            ctx.fillStyle = "#5a2a16";
            ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.font = `700 30px ${font}`;
            ctx.letterSpacing = "3px";
            ctx.fillText(title, x + w / 2, y + 44);
            ctx.letterSpacing = "0px";
            icon(title, x + w / 2, y + 140, 100);
            ctx.fillStyle = "#7a4a26";
            ctx.font = `italic 600 22px ${font}`;
            ctx.fillText(subtitles[title], x + w / 2, y + h - 40);
        }
        // Rewers: bordo z romboidalnym wzorem i gwiazdą.
        {
            const { x, y, w, h } = ATLAS.back;
            ctx.fillStyle = "#3a1210"; rounded(x, y, w, h, 14); ctx.fill();
            ctx.fillStyle = "#6e221c"; rounded(x + 8, y + 8, w - 16, h - 16, 10); ctx.fill();
            ctx.strokeStyle = "rgba(214,164,96,0.55)"; ctx.lineWidth = 2;
            for (let i = -6; i < 12; i++) { ctx.beginPath(); ctx.moveTo(x + 8 + i * 24, y + 8); ctx.lineTo(x + 8 + i * 24 + 264, y + h - 8); ctx.stroke(); ctx.beginPath(); ctx.moveTo(x + 8 + i * 24 + 264, y + 8); ctx.lineTo(x + 8 + i * 24, y + h - 8); ctx.stroke(); }
            ctx.fillStyle = "#6e221c"; ctx.beginPath(); ctx.arc(x + w / 2, y + h / 2, 40, 0, Math.PI * 2); ctx.fill();
            star(ctx, x + w / 2, y + h / 2, 30, 13, "#d9a652");
        }
        // Znacznik rundy: drewniany krążek "RUNDA I".
        {
            const { x, y, w } = ATLAS.round, r = w / 2;
            const disc = ctx.createRadialGradient(x + r, y + r, 10, x + r, y + r, r);
            disc.addColorStop(0, "#9a6a3a"); disc.addColorStop(1, "#5a3a1e");
            ctx.fillStyle = disc; ctx.beginPath(); ctx.arc(x + r, y + r, r - 2, 0, Math.PI * 2); ctx.fill();
            ctx.strokeStyle = "#f0d08a"; ctx.lineWidth = 6; ctx.beginPath(); ctx.arc(x + r, y + r, r - 16, 0, Math.PI * 2); ctx.stroke();
            ctx.fillStyle = "#f0d08a"; ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.font = `700 34px ${font}`; ctx.fillText("RUNDA", x + r, y + r - 44);
            ctx.font = `700 96px ${font}`; ctx.fillText("I", x + r, y + r + 26);
        }
        // Notatka pojedynku: zasady wkrótce.
        {
            const { x, y, w, h } = ATLAS.rules;
            ctx.fillStyle = "#eadbb6"; ctx.fillRect(x, y, w, h);
            ctx.fillStyle = "rgba(120,80,40,0.25)"; ctx.fillRect(x, y + h - 30, w, 30);
            ctx.fillStyle = "#4a2614"; ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.font = `700 28px ${font}`; ctx.fillText("ŚLADEM", x + w / 2, y + 44);
            ctx.fillText("REWOLWEROWCA", x + w / 2, y + 80);
            ctx.font = `italic 600 24px ${font}`; ctx.fillStyle = "#7a4a26"; ctx.fillText("zasady pojedynku — wkrótce", x + w / 2, y + 124);
            ctx.fillStyle = "rgba(74,38,20,0.55)";
            for (let l = 0; l < 4; l++) ctx.fillRect(x + 40, y + 160 + l * 22, w - 80 - (l % 2) * 60, 3);
        }
        // Emblemat na suknie: złote pierścienie i gwiazda (tło przezroczyste — alphaTest).
        {
            const { x, y, w } = ATLAS.emblem, r = w / 2;
            ctx.strokeStyle = "rgba(214,164,96,0.85)"; ctx.lineWidth = 5;
            ctx.beginPath(); ctx.arc(x + r, y + r, r - 8, 0, Math.PI * 2); ctx.stroke();
            ctx.lineWidth = 2;
            ctx.beginPath(); ctx.arc(x + r, y + r, r - 26, 0, Math.PI * 2); ctx.stroke();
            star(ctx, x + r, y + r, 70, 30, "rgba(214,164,96,0.85)");
        }
    }, false);
}

function star(ctx, cx, cy, outer, inner, color) {
    ctx.fillStyle = color;
    ctx.beginPath();
    for (let i = 0; i < 10; i++) {
        const a = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 ? inner : outer;
        ctx.lineTo(cx + Math.cos(a) * r, cy + Math.sin(a) * r);
    }
    ctx.closePath();
    ctx.fill();
}

// Prostokąt z atlasu rekwizytów jako płaska geometria (UV wycięte z tekstury), leżąca na blacie (y w górę).
function atlasPlane(region, w, h, x, y, z, rotation = 0) {
    const geometry = new THREE.PlaneGeometry(w, h);
    const uv = geometry.attributes.uv, size = 1024;
    for (let i = 0; i < uv.count; i++) {
        const u = uv.getX(i), v = uv.getY(i);
        uv.setXY(i, (region.x + u * region.w) / size, 1 - (region.y + (1 - v) * region.h) / size);
    }
    geometry.rotateX(-Math.PI / 2);
    geometry.rotateY(rotation);
    geometry.translate(x, y, z);
    return geometry;
}

// Obraz w ramie: pustynny zachód słońca (mesy, słońce, ziemia).
function paintingTexture(seed, night) {
    return canvasTexture(256, 176, (ctx, w, h) => {
        const rnd = random(seed);
        const sky = ctx.createLinearGradient(0, 0, 0, h);
        sky.addColorStop(0, night ? "#1d2140" : "#e59a5a");
        sky.addColorStop(0.65, night ? "#6a3a4a" : "#f6d19a");
        sky.addColorStop(1, "#8a5230");
        ctx.fillStyle = sky;
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = night ? "#f1e6c8" : "#fff0c8";
        ctx.beginPath(); ctx.arc(w * (0.3 + rnd() * 0.4), h * 0.42, night ? 12 : 18, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = "#5a2e1c";
        ctx.beginPath();
        ctx.moveTo(0, h * 0.72);
        for (let x = 0; x <= w; x += 32) ctx.lineTo(x, h * (0.56 + rnd() * 0.12));
        ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.fill();
        ctx.fillStyle = "#3a1d12";
        ctx.fillRect(0, h * 0.84, w, h);
        for (let i = 0; i < 400; i++) {
            ctx.fillStyle = `rgba(0,0,0,${rnd() * 0.06})`;
            ctx.fillRect(rnd() * w, rnd() * h, 3, 3);
        }
    }, false);
}

function keysTexture() {
    return canvasTexture(256, 32, (ctx, w, h) => {
        ctx.fillStyle = "#f2ead8";
        ctx.fillRect(0, 0, w, h);
        for (let i = 0; i < 26; i++) {
            ctx.fillStyle = "#3a2a1c";
            ctx.fillRect(i * (w / 26), 0, 1, h);
            if (![2, 6, 9, 13, 16, 20, 23].includes(i)) ctx.fillRect(i * (w / 26) + 6, 0, 5, h * 0.6);
        }
    }, false);
}

// Miękka poświata wokół źródeł światła (sprite'y jako Points: jedno wywołanie rysowania na grupę).
function haloTexture() {
    return canvasTexture(64, 64, (ctx, w) => {
        const g = ctx.createRadialGradient(w / 2, w / 2, 0, w / 2, w / 2, w / 2);
        g.addColorStop(0, "rgba(255,236,200,1)");
        g.addColorStop(0.25, "rgba(255,196,120,0.55)");
        g.addColorStop(1, "rgba(255,160,80,0)");
        ctx.fillStyle = g;
        ctx.fillRect(0, 0, w, w);
    }, false);
}

// ---- Materiały ----------------------------------------------------------------------------------------------------

function materials(font) {
    const std = (color, map, tile, extra = {}) => {
        const material = new THREE.MeshStandardMaterial({ color, map, roughness: 0.78, metalness: 0, ...extra });
        material.userData.tile = tile;
        return material;
    };
    // Drewno: kolor z tekstury (zróżnicowane deski), wypukłość z tej samej tekstury. Pięć tekstur desek na całą salę —
    // materiały dzielą je i różnią się odcieniem (tint), który motyw mnoży przez swój mnożnik drewna.
    const maps = {
        floor: woodTexture(9, "#7a4e2e", { boards: 7, horizontal: true, staggered: true, knots: 5, nails: true }),
        panel: woodTexture(3, "#6a3c20", { boards: 6, knots: 2 }),
        beam: woodTexture(5, "#4a2c18", { boards: 4, horizontal: true, knots: 4, size: 256 }),
        top: woodTexture(8, "#2e170a", { boards: 3, horizontal: true, knots: 0, size: 256 }),
        furniture: woodTexture(10, "#74462a", { boards: 5, knots: 2, size: 256 })
    };
    const wood = (map, tint, tile, extra = {}) => {
        const material = std(tint, maps[map], tile, { bumpMap: maps[map], bumpScale: 1.4, ...extra });
        material.userData.tint = new THREE.Color(tint);
        return material;
    };
    const glow = (color, emissive) => new THREE.MeshStandardMaterial({ color, emissive, emissiveIntensity: 1, roughness: 0.4 });
    const wall = std("#ffffff", damaskTexture(), 1.4, { roughness: 0.9 });
    const mirrorMap = mirrorTexture();
    return {
        floor: wood("floor", "#ffffff", 3.2, { roughness: 0.7 }),
        wall,
        wainscot: wood("panel", "#d2bfb0", 1.4),
        trim: wood("panel", "#a08878", 1),
        beam: wood("beam", "#c4b4aa", 1.8),
        ceiling: wood("beam", "#e6d6cc", 2.4),
        bar: wood("panel", "#dcae94", 1.2, { roughness: 0.55 }),
        barTop: wood("top", "#ffffff", 2.4, { roughness: 0.28 }),
        wood: wood("furniture", "#ffffff", 1),
        darkWood: wood("panel", "#86705f", 1),
        bottle: std("#243b26", null, 1, { roughness: 0.2, metalness: 0.1 }),
        felt: std("#6b4128", noiseTexture(4, 150, 1500), 1, { roughness: 0.95 }),   // sukno pojedynku (tabaczkowe, nie kasynowa zieleń)
        lacquer: std("#23140c", null, 1, { roughness: 0.28, metalness: 0.05 }),     // lakier pianina
        props: new THREE.MeshStandardMaterial({ map: propsTexture(font), alphaTest: 0.5, roughness: 0.75 }),
        brass: std("#c99a45", null, 1, { roughness: 0.3, metalness: 0.8, side: THREE.DoubleSide }),
        iron: std("#2b2521", null, 1, { roughness: 0.55, metalness: 0.45, side: THREE.DoubleSide }),
        leather: std("#5c2217", null, 1, { roughness: 0.55 }),
        mirror: new THREE.MeshStandardMaterial({ map: mirrorMap, emissive: "#ffffff", emissiveMap: mirrorMap, emissiveIntensity: 0.5, roughness: 0.22, metalness: 0.35 }),
        runner: new THREE.MeshStandardMaterial({ map: runnerTexture(), roughness: 0.95 }),
        rug: std("#4f3a26", null, 2, { roughness: 1 }),
        paper: std("#ece0c6", null, 1),
        bottleGlass: new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.16, metalness: 0.12 }),   // kolor w wierzchołkach
        clearGlass: new THREE.MeshStandardMaterial({ color: "#e8f0f0", roughness: 0.08, metalness: 0.1, transparent: true, opacity: 0.35 }),
        cactus: std("#4f7a3a", null, 1, { roughness: 0.8 }),
        pot: std("#a5552f", null, 1),
        wax: new THREE.MeshStandardMaterial({ color: "#f3e6c8", emissive: "#ffb85c", emissiveIntensity: 0.6, roughness: 0.6 }),
        window: glow("#fff1d6", "#fff1d6"),
        bulb: glow("#fff1cf", "#ffcf7a"),
        barBulb: glow("#fff1cf", "#ffc86e"),
        gameBulb: glow("#fff1cf", "#ffd98f"),
        musicBulb: glow("#fff1cf", "#ffc86e"),
        keys: new THREE.MeshStandardMaterial({ map: keysTexture(), roughness: 0.4 }),
        chalk: (() => {
            // Kreda lekko "świeci" jak szyldy — zapowiedź czytelna także nocą.
            const map = chalkTexture(font);
            const material = new THREE.MeshStandardMaterial({ map, emissive: "#ffffff", emissiveMap: map, emissiveIntensity: 0.2, roughness: 0.9 });
            material.userData.sign = true;
            return material;
        })()
    };
}

// Szyld strefy w scenie (podświetlany własną teksturą — czytelny także nocą; siłę ustawia motyw).
function plaque(texture, w, h) {
    const material = new THREE.MeshStandardMaterial({ map: texture, emissive: "#ffffff", emissiveMap: texture, emissiveIntensity: 0.2, roughness: 0.5, metalness: 0.1 });
    material.userData.sign = true;
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, h), material);
    mesh.receiveShadow = true;
    return mesh;
}

// Rama szyldu: zaokrąglony prostokąt z fazą (bevel) — głębia i krawędź, która łapie światło lamp.
// Front ramy wypada 6,5 cm przed punktem (x, y, z) w kierunku frontu szyldu (ry obraca +z na ten kierunek).
function plaqueFrame(b, material, w, h, x, y, z, ry = 0) {
    const shape = new THREE.Shape(), r = 0.05, W = w / 2, H = h / 2;
    shape.moveTo(-W + r, -H);
    shape.lineTo(W - r, -H); shape.quadraticCurveTo(W, -H, W, -H + r);
    shape.lineTo(W, H - r); shape.quadraticCurveTo(W, H, W - r, H);
    shape.lineTo(-W + r, H); shape.quadraticCurveTo(-W, H, -W, H - r);
    shape.lineTo(-W, -H + r); shape.quadraticCurveTo(-W, -H, -W + r, -H);
    const geometry = new THREE.ExtrudeGeometry(shape, { depth: 0.04, bevelEnabled: true, bevelThickness: 0.02, bevelSize: 0.025, bevelSegments: 2, curveSegments: 4 });
    b.add(material, geometry, x, y, z, 0, ry, 0);
}

// ---- Instancje (krzesła, stołki, butelki, szklanki, żetony, żarówki): jedna geometria + materiał = jedno wywołanie ----

function instanced(root, geometry, material, transforms, colors) {
    const mesh = new THREE.InstancedMesh(geometry, material, transforms.length);
    const m = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler(), p = new THREE.Vector3(), s = new THREE.Vector3();
    transforms.forEach(([x, y, z, ry = 0, scale = 1], i) => {
        q.setFromEuler(e.set(0, ry, 0));
        mesh.setMatrixAt(i, m.compose(p.set(x, y, z), q, s.setScalar(scale)));
        if (colors) mesh.setColorAt(i, new THREE.Color(colors[i % colors.length]));
    });
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    root.add(mesh);
    return mesh;
}

// Geometria złożona z kilku brył (np. krzesło) scalona raz — potem współdzielona przez wszystkie instancje.
function parts(list, tile = 1) {
    return mergeGeometries(list.map(([geometry, x, y, z, rx = 0, ry = 0, rz = 0]) => {
        geometry.applyMatrix4(new THREE.Matrix4().makeRotationFromEuler(new THREE.Euler(rx, ry, rz)).setPosition(x, y, z));
        worldUv(geometry, tile);
        return geometry;
    }));
}

const box = (w, h, d) => new THREE.BoxGeometry(w, h, d);
const cylinder = (rt, rb, h, segments = 12) => new THREE.CylinderGeometry(rt, rb, h, segments);

// Drobne, deterministyczne odchylenia — mniej "technicznej" symetrii, ten sam wygląd przy każdym wejściu.
const jitter = random(77);
const wobble = amount => (jitter() - 0.5) * 2 * amount;

// ---- Budowa sali --------------------------------------------------------------------------------------------------

function buildRoom(root, b, M) {
    // Podłoga, sufit z belkami i żelaznymi okuciami, ściany: boazeria + tapeta + listwy + gzyms pod sufitem.
    b.box(M.floor, HALF_W * 2, 0.1, DEPTH, 0, -0.05, -DEPTH / 2);
    b.box(M.ceiling, HALF_W * 2, 0.12, DEPTH, 0, HEIGHT + 0.06, -DEPTH / 2);
    for (const z of [-1.4, -4.1, -6.8, -9.5, -12.1]) {
        b.box(M.beam, HALF_W * 2, 0.3, 0.34, 0, HEIGHT - 0.15, z);
        for (const x of [-3.3, 3.3]) b.box(M.iron, 0.34, 0.06, 0.4, x, HEIGHT - 0.32, z);
    }
    for (const x of [-3.3, 3.3]) b.box(M.beam, 0.26, 0.22, DEPTH, x, HEIGHT - 0.4, -DEPTH / 2);

    for (const [x, z, side] of [[0, -DEPTH, false], [-HALF_W, -DEPTH / 2, true], [HALF_W, -DEPTH / 2, true]]) {
        const length = side ? DEPTH : HALF_W * 2;
        const inward = side ? -Math.sign(x) : 1;
        const piece = (material, depth, height, y, inset) => side
            ? b.box(material, depth, height, length, x + inward * inset, y, z)
            : b.box(material, length, height, depth, x, y, z + inset);
        piece(M.wall, 0.2, HEIGHT, HEIGHT / 2, 0);
        piece(M.wainscot, 0.05, 1.15, 0.575, 0.12);
        piece(M.trim, 0.12, 0.09, 1.19, 0.13);        // listwa nad boazerią
        piece(M.trim, 0.09, 0.16, 0.08, 0.13);        // cokół
        piece(M.trim, 0.2, 0.18, HEIGHT - 0.09, 0.15); // gzyms
        piece(M.trim, 0.06, 0.05, 2.9, 0.12);         // listwa na obrazy
    }

    // Ściana wejściowa z drzwiami i dwoma oknami (za kamerą; światło dzienne wpada przez otwory).
    const doorHalf = 1.1, doorTop = 3.0, winX = 4.2, winHalf = 0.8, winBottom = 1.0, winTop = 2.8;
    const front = (x0, x1, y0, y1) => b.box(M.wall, x1 - x0, y1 - y0, 0.25, (x0 + x1) / 2, (y0 + y1) / 2, 0.1);
    front(-doorHalf, doorHalf, doorTop, HEIGHT);
    for (const s of [-1, 1]) {
        const edges = [s * doorHalf, s * (winX - winHalf), s * (winX + winHalf), s * HALF_W];
        const span = (a, c) => [Math.min(edges[a], edges[c]), Math.max(edges[a], edges[c])];
        front(...span(0, 1), 0, HEIGHT);
        front(...span(1, 2), 0, winBottom);
        front(...span(1, 2), winTop, HEIGHT);
        front(...span(2, 3), 0, HEIGHT);
        b.box(M.window, winHalf * 2, winTop - winBottom, 0.04, s * winX, (winBottom + winTop) / 2, 0.2);
        b.box(M.trim, winHalf * 2 + 0.2, 0.12, 0.2, s * winX, winTop + 0.06, 0.0);
        b.box(M.trim, winHalf * 2 + 0.3, 0.1, 0.3, s * winX, winBottom - 0.05, -0.05);
        b.box(M.trim, 0.05, winTop - winBottom, 0.08, s * winX, (winBottom + winTop) / 2, 0.02);
    }
    b.box(M.window, doorHalf * 2, doorTop, 0.04, 0, doorTop / 2, 0.3);

    // Chodnik od wejścia do baru — prowadzi wzrok do centrum sceny.
    const runner = new THREE.Mesh(new THREE.PlaneGeometry(1.7, 7.4), M.runner);
    runner.rotation.x = -Math.PI / 2;
    runner.position.set(0, 0.012, -5.2);
    runner.receiveShadow = true;
    root.add(runner);
}

// Butelki: cztery sylwetki toczone (niska szeroka, klasyczna smukła, z długą szyjką, mała karafka z korkiem),
// ustawione grupami jak kolekcja baru; paleta western: bursztyn, ciemna zieleń, dym, ciemny brąz. Wszystkie butelki
// i napój w szklankach to jedna geometria z kolorem w wierzchołkach — jeden draw call.
const BOTTLES = {
    squat: [[0, 0], [0.062, 0], [0.068, 0.014], [0.068, 0.13], [0.058, 0.158], [0.026, 0.178], [0.019, 0.19], [0.019, 0.228], [0.023, 0.232], [0.023, 0.248], [0, 0.248]],
    slim: [[0, 0], [0.042, 0], [0.046, 0.012], [0.046, 0.19], [0.038, 0.228], [0.02, 0.262], [0.016, 0.285], [0.016, 0.34], [0.019, 0.345], [0.019, 0.36], [0, 0.36]],
    long: [[0, 0], [0.038, 0], [0.041, 0.012], [0.041, 0.15], [0.028, 0.2], [0.014, 0.245], [0.012, 0.37], [0.015, 0.376], [0.015, 0.39], [0, 0.39]],
    carafe: [[0, 0], [0.048, 0], [0.066, 0.04], [0.067, 0.078], [0.05, 0.118], [0.021, 0.148], [0.019, 0.176], [0.026, 0.182], [0.016, 0.192], [0.03, 0.214], [0.018, 0.238], [0, 0.244]]
};
const GLASS = { amber: "#8a5219", darkAmber: "#653512", green: "#24452b", smoke: "#5e5750", brown: "#3e2515", honey: "#9a6a2e" };
const BOTTLE_WIDTH = { squat: 0.166, slim: 0.122, long: 0.112, carafe: 0.166 };

function buildBottles(root, M, glasses) {
    const pick = random(31);
    const shapes = Object.fromEntries(Object.entries(BOTTLES).map(([kind, points]) =>
        [kind, new THREE.LatheGeometry(points.map(([r, y]) => new THREE.Vector2(r, y)), 8)]));
    const pieces = [];
    const tint = new THREE.Color();
    const put = (geometry, color, x, y, z, ry = 0) => {
        const g = geometry.clone().applyMatrix4(new THREE.Matrix4().makeRotationY(ry).setPosition(x, y, z));
        // Ta sama butelka nigdy nie ma identycznego odcienia — lekka różnica jasności w grupie.
        pieces.push([g, tint.set(color).multiplyScalar(0.92 + pick() * 0.16).clone()]);
    };

    // Półki (y 1.6 / 2.2 / 2.8) po obu stronach lustra: grupy [rodzaj, liczba, kolor], wyśrodkowane na półce.
    const shelves = {
        [-1]: [[["squat", 3, "amber"], ["carafe", 1, "smoke"], ["squat", 2, "darkAmber"]],
               [["slim", 5, "green"], ["slim", 4, "amber"]],
               [["long", 4, "brown"], ["slim", 2, "smoke"], ["long", 3, "green"]]],
        [1]: [[["squat", 2, "amber"], ["squat", 2, "brown"], ["carafe", 1, "honey"]],
              [["slim", 4, "darkAmber"], ["carafe", 1, "smoke"], ["slim", 4, "green"]],
              [["long", 3, "amber"], ["slim", 3, "brown"], ["long", 3, "smoke"]]]
    };
    for (const side of [-1, 1]) {
        [1.6, 2.2, 2.8].forEach((y, row) => {
            const groups = shelves[side][row];
            const total = groups.reduce((sum, [kind, n]) => sum + n * BOTTLE_WIDTH[kind], 0) + (groups.length - 1) * 0.1;
            let x = side * 2.67 - total / 2;
            for (const [kind, n, color] of groups) {
                for (let i = 0; i < n; i++) {
                    put(shapes[kind], GLASS[color], x + BOTTLE_WIDTH[kind] / 2, y + 0.025, -12.2 + (pick() - 0.5) * 0.05, pick() * 6);
                    x += BOTTLE_WIDTH[kind];
                }
                x += 0.1;
            }
        });
    }

    // Blat zaplecza: kilka butelek po bokach lustra (lustro zostaje czyste).
    for (const [kind, color, x] of [["carafe", "amber", -3.05], ["squat", "darkAmber", -2.84], ["squat", "brown", -2.66],
                                    ["slim", "green", 2.62], ["long", "smoke", 2.76], ["slim", "amber", 2.9]]) {
        put(shapes[kind], GLASS[color], x, 1.06, -12.05, pick() * 6);
    }

    // Lada: butelka whisky przy dwóch szklankach; napój (bursztyn) w każdej szklance.
    put(shapes.squat, GLASS.amber, 0.62, 1.16, -10.12, 0.4);
    for (const [x, z] of glasses) put(cylinder(0.034, 0.031, 0.04, 10), GLASS.honey, x, 1.182, z);

    const merged = mergeGeometries(pieces.map(([g]) => g));
    const colors = new Float32Array(merged.attributes.position.count * 3);
    let offset = 0;
    for (const [g, color] of pieces) {
        for (let i = 0; i < g.attributes.position.count; i++, offset++) color.toArray(colors, offset * 3);
        g.dispose();
    }
    merged.setAttribute("color", new THREE.BufferAttribute(colors, 3));
    Object.values(shapes).forEach(g => g.dispose());
    const mesh = new THREE.Mesh(merged, M.bottleGlass);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    root.add(mesh);
}

function buildBar(root, b, M, font) {
    // Lada: korpus, wypukłe płyciny, gruby blat z zaokrągloną krawędzią, mosiężna poręcz na wspornikach.
    b.box(M.bar, 7.0, 1.08, 0.85, 0, 0.54, -10.2);
    b.box(M.barTop, 7.35, 0.08, 1.08, 0, 1.12, -10.2);
    b.add(M.barTop, cylinder(0.055, 0.055, 7.35, 10), 0, 1.11, -9.67, 0, 0, Math.PI / 2);
    for (let x = -3.0; x <= 3.01; x += 1.0) {
        b.box(M.trim, 0.82, 0.74, 0.03, x, 0.58, -9.765);
        b.box(M.bar, 0.66, 0.58, 0.03, x, 0.58, -9.74);
    }
    b.box(M.trim, 7.05, 0.1, 0.05, 0, 0.06, -9.76);
    b.add(M.brass, cylinder(0.035, 0.035, 7.0, 10), 0, 0.2, -9.55, 0, 0, Math.PI / 2);
    for (let x = -3.2; x <= 3.21; x += 1.6) b.add(M.brass, cylinder(0.02, 0.02, 0.25, 6), x, 0.2, -9.65, Math.PI / 2);
    // Kran do piwa i kilka szklanek na blacie: przy butelce na prawo od środka i przy kranie — poza kadrami stref
    // (Wanted i Muzyka patrzą z lady w bok, więc nic nie wchodzi im na pierwszy plan).
    b.add(M.brass, cylinder(0.06, 0.08, 0.42, 12), 1.6, 1.37, -10.35);
    for (const dx of [-0.08, 0, 0.08]) b.add(M.brass, cylinder(0.012, 0.012, 0.16, 6), 1.6 + dx, 1.5, -10.25, Math.PI / 2.4);
    const glasses = [[0.8, -10.02], [0.95, -10.2], [1.92, -10.08]];
    instanced(root, cylinder(0.042, 0.036, 0.095, 12), M.clearGlass, glasses.map(([x, z]) => [x, 1.2075, z]));

    // Zaplecze: szafka z płycinami, pilastry, gzyms z łukiem nad lustrem, półki na wspornikach.
    b.box(M.darkWood, 7.3, 1.0, 0.62, 0, 0.5, -12.12);
    for (let x = -3.3; x <= 3.31; x += 1.1) b.box(M.bar, 0.9, 0.7, 0.02, x, 0.5, -11.8);
    b.box(M.barTop, 7.4, 0.06, 0.68, 0, 1.03, -12.12);
    for (const x of [-3.62, -1.72, 1.72, 3.62]) {
        b.box(M.bar, 0.24, 2.1, 0.16, x, 2.1, -12.3);
        b.box(M.trim, 0.3, 0.12, 0.2, x, 1.1, -12.28);
    }
    b.box(M.bar, 7.6, 0.72, 0.3, 0, 3.56, -12.28);           // fryz — tło szyldu BAR
    b.box(M.trim, 7.8, 0.08, 0.36, 0, 3.96, -12.26);
    b.box(M.trim, 7.7, 0.06, 0.34, 0, 3.2, -12.26);
    const arch = new THREE.Shape();
    arch.absellipse(0, 0, 1.6, 0.2, 0, Math.PI, false);
    arch.lineTo(-1.6, 0);
    b.add(M.bar, new THREE.ExtrudeGeometry(arch, { depth: 0.08, bevelEnabled: false, curveSegments: 20 }), 0, 2.96, -12.36);

    // Lustro barowe z ciepłym odbiciem (tekstura podświetlona — "odbija" lampy i butelki).
    const mirror = new THREE.Mesh(new THREE.PlaneGeometry(3.2, 1.8), M.mirror);
    mirror.position.set(0, 2.05, -12.33);
    root.add(mirror);
    b.box(M.brass, 3.3, 0.04, 0.05, 0, 2.97, -12.31);
    b.box(M.brass, 3.3, 0.04, 0.05, 0, 1.13, -12.31);

    for (const s of [-1, 1]) {
        for (const y of [1.6, 2.2, 2.8]) {
            b.box(M.darkWood, 1.62, 0.05, 0.32, s * 2.67, y, -12.22);
            b.box(M.brass, 1.62, 0.02, 0.02, s * 2.67, y + 0.03, -12.07);
        }
    }

    // Szyld BAR na fryzie: złocona deska z dwiema lampkami obrazowymi nad nim.
    const { w: signW, h: signH, center: signAt } = ZONES.bar.sign;
    const sign = plaque(plaqueTexture("BAR", font, { size: 0.62, height: 176 }), signW, signH);
    sign.position.set(...signAt);
    root.add(sign);
    plaqueFrame(b, M.trim, signW + 0.16, signH + 0.16, 0, signAt[1], signAt[2] - 0.065);
    for (const x of [-0.9, 0.9]) {
        b.add(M.brass, cylinder(0.015, 0.015, 0.26, 6), x, 4.02, -12.0, 1.2);
        b.add(M.brass, new THREE.ConeGeometry(0.09, 0.14, 12, 1, true), x, 3.99, -11.9, Math.PI - 0.5);
    }

    buildBottles(root, M, glasses);

    // Stołki barowe: siedzisko i noga (dwie instancje), lekko obrócone; jeden odsunięty od lady.
    const stools = [];
    for (let x = -3; x <= 3.01; x += 1) stools.push([x + wobble(0.06), 0, -9.25 + (Math.abs(x - 1) < 0.01 ? 0.32 : wobble(0.05)), wobble(0.4)]);
    instanced(root, parts([[cylinder(0.24, 0.22, 0.09, 14), 0, 0.84, 0], [cylinder(0.25, 0.25, 0.02, 14), 0, 0.885, 0]]), M.leather, stools);
    instanced(root, parts([[cylinder(0.04, 0.05, 0.8, 8), 0, 0.4, 0], [cylinder(0.22, 0.25, 0.04, 14), 0, 0.02, 0],
        [new THREE.TorusGeometry(0.17, 0.018, 6, 16), 0, 0.3, 0, Math.PI / 2]]), M.brass, stools);

    // Lampy wiszące nad barem; kluczowe światło baru (reflektor na zaplecze, z cieniem) + wypełniające.
    // Cztery lampy — środek wolny, żeby nic nie zasłaniało szyldu BAR ani lustra.
    const pendants = [[-3.1, 3.2, -10.2], [-1.6, 3.25, -10.2], [1.6, 3.22, -10.2], [3.1, 3.2, -10.2]];
    instanced(root, parts([[new THREE.ConeGeometry(0.34, 0.3, 16, 1, true), 0, 0, 0], [cylinder(0.01, 0.01, 1.0, 4), 0, 0.62, 0]]), M.iron, pendants);
    instanced(root, new THREE.SphereGeometry(0.09, 12, 8), M.barBulb, pendants.map(([x, y, z]) => [x, y - 0.12, z]));
    const key = new THREE.SpotLight("#ffc27a", 1, 13, 0.62, 0.75, 1.25);
    key.position.set(0, 4.15, -8.4);
    key.target.position.set(0, 1.9, -12.3);
    key.castShadow = true;
    key.shadow.mapSize.set(1024, 1024);
    key.shadow.bias = -0.0006;
    key.shadow.normalBias = 0.02;
    root.add(key, key.target);
    const fills = [-1.8, 1.8].map(x => {
        const light = new THREE.PointLight("#ffbf6e", 1, 10, 1.5);
        light.position.set(x, 2.9, -9.9);
        root.add(light);
        return light;
    });
    return {
        glows: [sign.material, M.barBulb], lights: fills, key,
        halos: [...pendants.map(([x, y, z]) => [x, y - 0.14, z, 1]), [-0.9, 3.93, -11.88, 0.5], [0.9, 3.93, -11.88, 0.5]]
    };
}

function buildWanted(root, b, M, font) {
    // Tablica na lewej ścianie (front w +x), widoczna z kadru głównego: rama z fazą, daszek na wspornikach, półka,
    // gwoździe w 3D przy listach gończych i neutralna, kierunkowa lampa pod daszkiem.
    const { center: [x, y, z], w, h } = ZONES.wanted.sign;
    const texture = wantedTexture(font);
    const board = plaque(texture, w, h);
    board.position.set(x, y, z);
    board.rotation.y = Math.PI / 2;
    root.add(board);
    plaqueFrame(b, M.trim, w + 0.36, h + 0.36, x - 0.065, y, z, Math.PI / 2);
    const roofY = y + h / 2 + 0.34;
    b.box(M.beam, 0.66, 0.06, w + 0.7, -6.1, roofY, z, 0, 0, -0.32);
    b.box(M.trim, 0.7, 0.04, w + 0.74, -6.1, roofY + 0.04, z, 0, 0, -0.32);
    for (const s of [-1, 1]) b.box(M.trim, 0.42, 0.06, 0.06, -6.2, roofY - 0.14, z + s * (w / 2 + 0.2), 0, 0, 0.6);
    b.box(M.trim, 0.2, 0.07, w + 0.44, -6.26, y - h / 2 - 0.24, z);

    // Gwoździe: przy pinezce każdego listu gończego (układ tekstury WANTED_CANVAS) i w rogach tablicy.
    const nails = [];
    const at = (u, v) => [x + 0.012, y + h / 2 - v * h, z - (u - 0.5) * w];
    const [cw, ch] = WANTED_CANVAS;
    for (const { x: px, y: py, h: ph } of [WANTED_MAIN, ...WANTED_SMALL]) nails.push(at(px / cw, (py - ph / 2 + 9) / ch));
    for (const [u, v] of [[0.02, 0.03], [0.98, 0.03], [0.02, 0.97], [0.98, 0.97]]) nails.push(at(u, v));
    for (const [nx, ny, nz] of nails) b.add(M.iron, cylinder(0.02, 0.02, 0.018, 10), nx, ny, nz, 0, 0, Math.PI / 2);   // w partii żelaza (bez osobnego draw calla)

    // Lampa pod daszkiem: neutralne, kierunkowe światło na tablicę.
    b.add(M.iron, new THREE.ConeGeometry(0.13, 0.16, 16, 1, true), -5.95, roofY - 0.2, z);
    b.add(M.bulb, new THREE.SphereGeometry(0.05, 10, 8), -5.95, roofY - 0.27, z);
    const light = new THREE.SpotLight("#fff0da", 1, 7, 0.85, 0.6, 1.3);
    light.position.set(-5.35, roofY - 0.1, z);
    light.target.position.set(-6.35, y - 0.1, z);
    root.add(light, light.target);
    return { glows: [board.material], lights: [light], halos: [[-5.95, roofY - 0.3, z, 0.45]], redraw: texture.userData.redraw };
}

function buildGame(root, b, M, font) {
    // Stół pojedynku 1 na 1: gruby blat, wyściełana obwódka, bordowe sukno z emblematem, toczona noga na krzyżaku.
    const x = 2.5, z = -5.9, top = 0.8;
    b.add(M.wood, cylinder(1.02, 0.97, 0.09, 36), x, top - 0.02, z);
    b.add(M.lacquer, new THREE.TorusGeometry(0.965, 0.05, 8, 48), x, top + 0.03, z, Math.PI / 2);
    b.add(M.felt, cylinder(0.92, 0.92, 0.02, 40), x, top + 0.03, z);
    const turned = [[0.24, 0], [0.24, 0.05], [0.1, 0.1], [0.075, 0.3], [0.12, 0.42], [0.065, 0.55], [0.085, 0.66], [0.2, 0.72], [0.2, 0.74]];
    b.add(M.darkWood, new THREE.LatheGeometry(turned.map(([r, py]) => new THREE.Vector2(r, py)), 16), x, 0, z);
    for (const a of [Math.PI / 4, -Math.PI / 4]) b.box(M.darkWood, 1.1, 0.07, 0.12, x, 0.05, z, 0, a);

    // Rekwizyty z jednej tekstury (jedna geometria): emblemat, pięć kart akcji na środku, zakryte karty graczy,
    // znacznik rundy i notatka z zasadami.
    const t = top + 0.042;
    const planes = [atlasPlane(ATLAS.emblem, 0.92, 0.92, x, t, z)];
    ATLAS.cards.forEach((card, i) => {
        const k = i - 2;
        planes.push(atlasPlane(card, 0.17, 0.24, x + k * 0.2, t + 0.004 + i * 0.001, z + 0.16 + Math.abs(k) * 0.03, -k * 0.1 + wobble(0.03)));
    });
    for (const side of [-1, 1]) for (let k = 0; k < 3; k++) {
        planes.push(atlasPlane(ATLAS.back, 0.17, 0.24, x + side * 0.6 + wobble(0.02), t + 0.004 + k * 0.002, z - 0.05 + wobble(0.02), side * Math.PI / 2 + wobble(0.12)));
    }
    planes.push(atlasPlane(ATLAS.round, 0.2, 0.2, x, t + 0.006, z - 0.42));
    planes.push(atlasPlane(ATLAS.rules, 0.3, 0.23, x + 0.52, t + 0.006, z + 0.5, -0.4));
    const propsMesh = new THREE.Mesh(mergeGeometries(planes), M.props);
    propsMesh.receiveShadow = true;
    root.add(propsMesh);

    // Znaczniki amunicji (naboje) i prestiżu (gwiazdy) przy każdym graczu — w partii mosiądzu (bez osobnych draw calli).
    for (const side of [-1, 1]) for (let i = 0; i < 6; i++) {
        const bx = x + side * (0.42 + (i % 2) * 0.05), bz = z - 0.34 + Math.floor(i / 2) * 0.07 + wobble(0.01);
        b.add(M.brass, cylinder(0.014, 0.014, 0.045, 8), bx, t + 0.0225, bz);
        b.add(M.brass, new THREE.ConeGeometry(0.014, 0.024, 8), bx, t + 0.057, bz);
    }
    const starShape = new THREE.Shape();
    for (let i = 0; i < 10; i++) {
        const a = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 ? 0.02 : 0.045;
        if (i === 0) starShape.moveTo(Math.cos(a) * r, Math.sin(a) * r); else starShape.lineTo(Math.cos(a) * r, Math.sin(a) * r);
    }
    for (const side of [-1, 1]) for (let i = 0; i < 3; i++) {
        const badge = new THREE.ExtrudeGeometry(starShape, { depth: 0.008, bevelEnabled: false });
        badge.rotateX(-Math.PI / 2);
        b.add(M.brass, badge, x + side * 0.72, t, z + 0.18 + i * 0.1, 0, wobble(0.6));
    }

    // Lampa: niska, ciepła, żelazny klosz — krąg światła tylko nad stołem.
    b.add(M.iron, new THREE.ConeGeometry(0.4, 0.26, 20, 1, true), x, 2.5, z);
    b.add(M.brass, new THREE.TorusGeometry(0.4, 0.012, 6, 32), x, 2.37, z, Math.PI / 2);
    b.add(M.iron, cylinder(0.012, 0.012, HEIGHT - 2.63, 4), x, (HEIGHT + 2.63) / 2, z);
    b.add(M.gameBulb, new THREE.SphereGeometry(0.08, 12, 8), x, 2.42, z);
    const light = new THREE.SpotLight("#ffbe74", 1, 5, 0.9, 0.5, 1.3);
    light.position.set(x, 2.38, z);
    light.target.position.set(x, 0.8, z);
    root.add(light, light.target);

    // Szyld "STÓŁ GRY" na łańcuchach (rama z fazą) i kredowa tabliczka "wkrótce".
    const { center, w, h } = ZONES.game.sign;
    const sign = plaque(plaqueTexture("STÓŁ GRY", font, { size: 0.46 }), w, h);
    sign.position.set(...center);
    root.add(sign);
    plaqueFrame(b, M.trim, w + 0.12, h + 0.12, center[0], center[1], center[2] - 0.065);
    for (const dx of [-0.42, 0.42]) b.add(M.iron, cylinder(0.008, 0.008, HEIGHT - center[1] - h / 2, 4), center[0] + dx, (HEIGHT + center[1] + h / 2) / 2, center[2] - 0.03);
    const tag = new THREE.Mesh(new THREE.PlaneGeometry(0.5, 0.19), M.chalk);
    tag.position.set(center[0] - 0.32, center[1] - h / 2 - 0.2, center[2] + 0.01);
    tag.rotation.z = -0.06;
    root.add(tag);

    // Dwa krzesła naprzeciw siebie — pojedynek jeden na jeden.
    const chairs = [[x - 1.3, 0, z + wobble(0.06), Math.PI / 2 + wobble(0.12)], [x + 1.3, 0, z + wobble(0.06), -Math.PI / 2 + wobble(0.12)]];
    return { glows: [M.gameBulb, sign.material], lights: [light], chairs, halos: [[x, 2.36, z, 0.6]] };
}

function buildMusic(root, b, M, font) {
    // Kącik muzyczny na podeście: pianino przy prawej ścianie (front w -x), stołek, gramofon na stoliku w rogu,
    // gitara na ścianie, lampka na pianinie, ciepłe boczne światło. Wolne miejsce przed klawiaturą.
    const base = 0.18;
    b.box(M.wood, 2.7, base, 4.0, 5.15, base / 2, -8.9);
    b.box(M.trim, 0.07, base + 0.02, 4.0, 3.79, base / 2, -8.9);
    b.box(M.rug, 2.0, 0.015, 3.0, 5.1, base + 0.008, -8.9);

    // Pianino (orzech, czarne wykończenia): korpus, pokrywa z nawisem, wysunięta klawiatura z policzkami, pulpit z nutami, płyciny, nogi, pedały.
    const px = 6.08, pz = -8.8;
    b.box(M.wood, 0.58, 1.28, 1.56, px, base + 0.64, pz);
    b.box(M.lacquer, 0.66, 0.05, 1.64, px - 0.02, base + 1.305, pz);
    b.box(M.lacquer, 0.38, 0.1, 1.5, px - 0.42, base + 0.72, pz);
    b.box(M.lacquer, 0.07, 0.14, 1.5, px - 0.6, base + 0.66, pz);
    for (const s of [-1, 1]) {
        b.box(M.lacquer, 0.42, 0.2, 0.06, px - 0.41, base + 0.74, pz + s * 0.78);
        b.box(M.darkWood, 0.02, 0.34, 0.6, px - 0.3, base + 1.02, pz + s * 0.38);    // płyciny górne
        b.box(M.lacquer, 0.12, 0.52, 0.09, px - 0.5, base + 0.26, pz + s * 0.7);    // nogi
        b.box(M.brass, 0.01, 0.1, 0.01, px - 0.3, base + 1.02, pz + s * 0.38 + 0.25);
    }
    b.box(M.darkWood, 0.02, 0.36, 1.3, px - 0.3, base + 0.32, pz);                  // płycina dolna
    b.box(M.lacquer, 0.03, 0.28, 0.95, px - 0.36, base + 0.95, pz, 0, 0, 0.22);     // pulpit
    b.box(M.paper, 0.012, 0.25, 0.2, px - 0.38, base + 0.96, pz - 0.11, 0, 0.05, 0.22);
    b.box(M.paper, 0.012, 0.25, 0.2, px - 0.38, base + 0.96, pz + 0.11, 0, -0.05, 0.22);
    for (const dz of [-0.08, 0, 0.08]) b.box(M.brass, 0.12, 0.02, 0.035, px - 0.36, base + 0.07, pz + dz);
    const keys = new THREE.Mesh(new THREE.PlaneGeometry(1.42, 0.28), M.keys);
    keys.rotation.set(-Math.PI / 2, 0, -Math.PI / 2);
    keys.position.set(px - 0.45, base + 0.772, pz);
    root.add(keys);

    // Stołek: okrągłe siedzisko na trzech nogach, lekko odsunięty i obrócony.
    const sx = 5.22, sz = -8.72;
    b.add(M.leather, cylinder(0.19, 0.19, 0.08, 20), sx, base + 0.52, sz);
    b.add(M.darkWood, cylinder(0.2, 0.18, 0.05, 20), sx, base + 0.47, sz);
    for (let i = 0; i < 3; i++) {
        const a = i / 3 * Math.PI * 2 + 0.3;
        b.box(M.darkWood, 0.04, 0.47, 0.04, sx + Math.cos(a) * 0.13, base + 0.23, sz + Math.sin(a) * 0.13);
    }

    // Lampka na pianinie.
    const lampZ = pz + 0.58, lampY = base + 1.33;
    b.add(M.brass, cylinder(0.06, 0.07, 0.03, 12), px - 0.05, lampY + 0.015, lampZ);
    b.add(M.brass, cylinder(0.012, 0.012, 0.3, 6), px - 0.05, lampY + 0.18, lampZ);
    b.add(M.iron, new THREE.ConeGeometry(0.1, 0.12, 14, 1, true), px - 0.05, lampY + 0.36, lampZ);
    b.add(M.musicBulb, new THREE.SphereGeometry(0.035, 10, 8), px - 0.05, lampY + 0.31, lampZ);

    // Gramofon na stoliku w rogu: toczona noga, skrzynka, płyta z etykietą, ramię, wygięta tuba.
    const gx = 5.8, gz = -10.3;
    b.add(M.darkWood, new THREE.LatheGeometry([[0.16, 0], [0.16, 0.03], [0.05, 0.06], [0.035, 0.4], [0.06, 0.6], [0.26, 0.64], [0.26, 0.67]].map(([r, py]) => new THREE.Vector2(r, py)), 16), gx, base, gz);
    b.box(M.wood, 0.36, 0.12, 0.36, gx, base + 0.73, gz, 0, 0.3);
    b.add(M.lacquer, cylinder(0.14, 0.14, 0.012, 28), gx, base + 0.796, gz);
    b.add(M.leather, cylinder(0.04, 0.04, 0.014, 16), gx, base + 0.798, gz);
    b.box(M.brass, 0.02, 0.02, 0.2, gx + 0.1, base + 0.82, gz - 0.02, 0, 0.6);
    const horn = [[0.012, 0], [0.016, 0.08], [0.028, 0.16], [0.06, 0.25], [0.12, 0.32], [0.2, 0.36]].map(([r, py]) => new THREE.Vector2(r, py));
    b.add(M.brass, new THREE.LatheGeometry(horn, 24), gx + 0.1, base + 0.8, gz + 0.08, 0, 0.5, 0.95);
    b.add(M.brass, cylinder(0.014, 0.014, 0.1, 8), gx + 0.12, base + 0.8, gz + 0.06, 0, 0, 0.95);

    // Gitara na ścianie (detal ścienny, front w -x).
    const gy = 1.95, gz2 = -7.3, wallX = 6.34;
    b.add(M.wood, cylinder(0.19, 0.19, 0.07, 22), wallX, gy - 0.12, gz2, 0, 0, Math.PI / 2);
    b.add(M.wood, cylinder(0.15, 0.15, 0.07, 22), wallX, gy + 0.14, gz2, 0, 0, Math.PI / 2);
    b.add(M.lacquer, cylinder(0.055, 0.055, 0.01, 16), wallX - 0.04, gy + 0.02, gz2, 0, 0, Math.PI / 2);
    b.box(M.darkWood, 0.04, 0.52, 0.06, wallX - 0.01, gy + 0.52, gz2);
    b.box(M.darkWood, 0.05, 0.13, 0.08, wallX, gy + 0.84, gz2);
    b.box(M.iron, 0.03, 0.05, 0.03, wallX + 0.03, gy + 0.92, gz2);

    // Szyld "MUZYKA" (rama z fazą) i kredowa tabliczka "wkrótce".
    const { center, w, h } = ZONES.music.sign;
    const sign = plaque(plaqueTexture("MUZYKA", font, { size: 0.5 }), w, h);
    sign.position.set(...center);
    sign.rotation.y = -Math.PI / 2;
    root.add(sign);
    plaqueFrame(b, M.trim, w + 0.14, h + 0.14, center[0] + 0.065, center[1], center[2], -Math.PI / 2);
    const tag = new THREE.Mesh(new THREE.PlaneGeometry(0.62, 0.23), M.chalk);
    tag.position.set(center[0] - 0.01, center[1] - h / 2 - 0.12, center[2] - 0.45);   // nad listwą na obrazy (y 2.9)
    tag.rotation.set(0, -Math.PI / 2, 0.05);
    root.add(tag);

    // Ciepłe światło boczne od strony sali, na front pianina i gramofon.
    const light = new THREE.SpotLight("#ffc07a", 1, 7, 0.8, 0.7, 1.3);
    light.position.set(4.5, 2.6, -7.4);
    light.target.position.set(5.9, 0.9, -9.1);
    root.add(light, light.target);
    return { glows: [M.musicBulb, sign.material], lights: [light], halos: [[px - 0.05, lampY + 0.3, lampZ, 0.4]] };
}

function buildFurniture(root, b, M, gameChairs) {
    // Stoliki z krzesłami (lekko wysunięte i obrócone), świece w butelkach na stolikach. Lewy tylny stolik odsunięty od
    // tablicy Wanted, żeby jej nie zasłaniał.
    const chairs = [...gameChairs];
    const candles = [];
    for (const [x, z] of [[-2.9, -4.1], [-3.3, -6.4], [4.9, -2.5]]) {
        b.add(M.wood, cylinder(0.72, 0.72, 0.06, 20), x, 0.8, z);
        b.add(M.darkWood, cylinder(0.08, 0.14, 0.77, 8), x, 0.385, z);
        b.add(M.darkWood, cylinder(0.4, 0.45, 0.05, 12), x, 0.025, z);
        for (const a of [0.3, 2.4, 4.4]) {
            const angle = a + wobble(0.25), r = 1.05 + wobble(0.15);
            chairs.push([x + Math.sin(angle) * r, 0, z + Math.cos(angle) * r, angle + Math.PI + wobble(0.35)]);
        }
        b.add(M.bottle, cylinder(0.05, 0.06, 0.2, 8), x + 0.15, 0.93, z - 0.1);
        candles.push([x + 0.15, 1.08, z - 0.1]);
    }
    // Krzesło z oparciem na szczeblach (jedna scalona geometria dla wszystkich instancji).
    const leg = [-0.19, 0.19].flatMap(lx => [-0.19, 0.19].map(lz => [box(0.05, 0.48, 0.05), lx, 0.24, lz]));
    const slats = [-0.1, 0, 0.1].map(sx => [box(0.04, 0.42, 0.025), sx, 0.78, -0.2]);
    instanced(root, parts([[box(0.46, 0.06, 0.46), 0, 0.48, 0], [box(0.05, 0.62, 0.05), -0.2, 0.8, -0.2], [box(0.05, 0.62, 0.05), 0.2, 0.8, -0.2],
        [box(0.46, 0.08, 0.05), 0, 1.07, -0.2], ...slats, ...leg]), M.wood, chairs);
    instanced(root, cylinder(0.022, 0.022, 0.1, 8), M.wax, candles);

    // Żyrandol (pierścień + świece jako instancje) i kinkiety.
    b.add(M.brass, new THREE.TorusGeometry(0.8, 0.04, 6, 32), 0, 3.84, -6.2, Math.PI / 2);
    b.add(M.brass, new THREE.TorusGeometry(0.32, 0.025, 6, 20), 0, 3.96, -6.2, Math.PI / 2);
    b.add(M.iron, cylinder(0.015, 0.015, 0.45, 4), 0, 4.15, -6.2);
    const ring = Array.from({ length: 10 }, (_, i) => {
        const a = i / 10 * Math.PI * 2;
        return [Math.cos(a) * 0.8, 3.95, -6.2 + Math.sin(a) * 0.8];
    });
    const sconces = [[-6.25, 2.7, -5.1], [6.25, 2.7, -4.0], [-4.5, 2.7, -12.25], [4.5, 2.7, -12.25]];
    for (const [x, y, z] of sconces) b.add(M.brass, cylinder(0.05, 0.08, 0.12, 8), x, y - 0.12, z);
    instanced(root, new THREE.SphereGeometry(0.075, 10, 8), M.bulb, [...ring, ...sconces]);
    const chandelier = new THREE.PointLight("#ffc98a", 1, 12, 1.4);
    chandelier.position.set(0, 3.55, -6.2);
    root.add(chandelier);

    // Obrazy w ramach, beczki i skrzynie w rogu, kaktus w donicy przy wejściu (znak SansPost).
    for (const [x, y, z, ry, seed, night] of [[-4.95, 2.35, -12.34, 0, 3, false], [6.34, 2.3, -4.6, -Math.PI / 2, 8, true]]) {
        const painting = new THREE.Mesh(new THREE.PlaneGeometry(1.1, 0.76), new THREE.MeshStandardMaterial({ map: paintingTexture(seed, night), roughness: 0.7 }));
        painting.position.set(x, y, z);
        painting.rotation.y = ry;
        root.add(painting);
        const frame = ry ? [0.06, 0.92, 1.26] : [1.26, 0.92, 0.06];
        b.box(M.brass, ...frame, x + (ry ? 0.03 : 0), y, z + (ry ? 0 : -0.03));
    }
    for (const [x, z, r] of [[-5.85, -11.85, 0.2], [-5.1, -11.95, -0.3], [-5.6, -11.1, 0.5]]) {
        b.add(M.wood, cylinder(0.36, 0.33, 0.95, 14), x, 0.475, z, 0, r);
        for (const y of [0.18, 0.72]) b.add(M.iron, cylinder(0.375, 0.375, 0.05, 14), x, y, z);
    }
    b.box(M.darkWood, 0.6, 0.5, 0.6, -4.5, 0.25, -11.9, 0, 0.3);
    b.box(M.darkWood, 0.45, 0.4, 0.45, -4.45, 0.7, -11.85, 0, -0.2);
    b.add(M.pot, cylinder(0.28, 0.22, 0.45, 12), 5.8, 0.225, -0.9);
    b.add(M.cactus, cylinder(0.14, 0.16, 1.1, 10), 5.8, 0.95, -0.9);
    b.add(M.cactus, cylinder(0.08, 0.09, 0.4, 8), 5.62, 1.05, -0.9, 0, 0, 0.9);
    b.add(M.cactus, cylinder(0.08, 0.09, 0.35, 8), 5.98, 1.2, -0.9, 0, 0, -0.9);
    return {
        chandelier,
        halos: [...ring.map(([x, y, z]) => [x, y, z, 0.35]), ...sconces.map(([x, y, z]) => [x, y, z, 0.55]), ...candles.map(([x, y, z]) => [x, y + 0.06, z, 0.3])]
    };
}

// Poświata lamp: dwie grupy Points (duże i małe) z addytywnym mieszaniem — tanie, a buduje nastrój.
function buildHalos(root, halos) {
    const texture = haloTexture();
    return [[h => h[3] >= 0.6, 1.1], [h => h[3] < 0.6, 0.5]].map(([filter, size]) => {
        const list = halos.filter(filter);
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute("position", new THREE.Float32BufferAttribute(list.flatMap(h => h.slice(0, 3)), 3));
        const points = new THREE.Points(geometry, new THREE.PointsMaterial({
            map: texture, size, sizeAttenuation: true, transparent: true, depthWrite: false,
            blending: THREE.AdditiveBlending, color: "#ffc98a"
        }));
        points.renderOrder = 2;
        root.add(points);
        return points.material;
    });
}

// Niewidzialne bryły trafień dla raycastingu: strefa i jej szyld (klik w szyld = klik w strefę). Wspólny materiał.
function buildHitAreas(root) {
    const material = new THREE.MeshBasicMaterial({ visible: false });
    return Object.entries(ZONES).flatMap(([zone, { hit: [[w, h, d], [x, y, z]], sign }]) => {
        const area = new THREE.Mesh(box(w, h, d), material);
        area.position.set(x, y, z);
        const [sx, sy, sz] = sign.center;
        const plate = new THREE.Mesh(sign.facing === "z" ? box(sign.w + 0.2, sign.h + 0.2, 0.2) : box(0.2, sign.h + 0.2, sign.w + 0.2), material);
        plate.position.set(sx, sy, sz);
        return [area, plate].map(mesh => {
            mesh.userData.zone = zone;
            root.add(mesh);
            return mesh;
        });
    });
}

function buildScene(font) {
    const scene = new THREE.Scene();
    const M = materials(font);
    const b = new Batch();
    buildRoom(scene, b, M);
    const zones = {
        bar: buildBar(scene, b, M, font),
        wanted: buildWanted(scene, b, M, font),
        game: buildGame(scene, b, M, font),
        music: buildMusic(scene, b, M, font)
    };
    const furniture = buildFurniture(scene, b, M, zones.game.chairs);
    b.build(scene);
    const halos = buildHalos(scene, [...Object.values(zones).flatMap(z => z.halos), ...furniture.halos]);

    const hemi = new THREE.HemisphereLight("#fff", "#000", 1);
    const sun = new THREE.DirectionalLight("#fff", 1);
    sun.castShadow = true;
    sun.shadow.mapSize.set(1024, 1024);
    Object.assign(sun.shadow.camera, { left: -9, right: 9, top: 9, bottom: -9, near: 1, far: 40 });
    sun.shadow.bias = -0.0005;
    sun.shadow.normalBias = 0.03;
    sun.target.position.set(0, 0, -6);
    scene.add(hemi, sun, sun.target);

    return { scene, M, zones, chandelier: furniture.chandelier, halos, hemi, sun, hits: buildHitAreas(scene) };
}

function applyTheme(s, colors) {
    s.scene.background = new THREE.Color(colors.outside);
    s.hemi.color.set(colors.hemiSky);
    s.hemi.groundColor.set(colors.hemiGround);
    s.hemi.intensity = colors.hemi;
    s.sun.color.set(colors.sun);
    s.sun.intensity = colors.sunIntensity;
    s.sun.position.set(...colors.sunPosition);
    s.M.window.color.set(colors.windowColor);
    s.M.window.emissive.set(colors.windowColor);
    s.M.window.emissiveIntensity = colors.windowGlow;
    s.M.wall.color.set(colors.wall);
    const woodTone = new THREE.Color(colors.wood);
    for (const material of Object.values(s.M)) if (material.userData?.tint) material.color.copy(material.userData.tint).multiply(woodTone);
    s.M.mirror.emissiveIntensity = colors.mirrorGlow;
    s.M.chalk.emissiveIntensity = colors.signGlow;
    s.zones.bar.key.intensity = colors.barKey;
    for (const material of s.halos) material.opacity = colors.halo;
    s.colors = colors;
    s.renderer.toneMappingExposure = colors.exposure;
    s.renderer.shadowMap.needsUpdate = true;
    highlight(s);
}

// Światła i poświata stref: baza z motywu, wzmocnienie dla strefy wskazanej (hover/fokus) albo wybranej.
function highlight(s) {
    const c = s.colors;
    s.chandelier.intensity = c.chandelier;
    s.M.bulb.emissiveIntensity = c.bulbs;
    for (const [zone, { lights, glows }] of Object.entries(s.zones)) {
        const active = zone === s.hover || zone === s.area;
        for (const light of lights) light.intensity = (zone === "bar" ? c.bar : c.accent) * (active ? 1.8 : 1);
        for (const material of glows) {
            material.emissiveIntensity = (material.userData.sign ? c.signGlow : c.bulbs) * (active ? 2.4 : 1);
        }
    }
    s.dirty = true;
}

// ---- Kamera -------------------------------------------------------------------------------------------------------

function aspectOf(s) {
    return (s.host.clientWidth || 1) / (s.host.clientHeight || 1);
}

function setView(s, view) {
    s.camera.position.set(...view.position);
    s.target.set(...view.target);
    s.camera.fov = view.fov;
    s.camera.updateProjectionMatrix();
    s.camera.lookAt(s.target);
    s.camera.updateMatrixWorld();
    s.dirty = true;
}

// Płynny przejazd (easeInOut); przy reduced motion — od razu. Promise kończy się po dojechaniu.
function moveTo(s, view, duration) {
    s.move?.resolve();
    if (duration <= 0 || reducedMotion()) {
        s.move = null;
        s.debug.moving = false;
        setView(s, view);
        return Promise.resolve();
    }
    s.debug.moving = true;
    return new Promise(resolve => {
        s.move = {
            from: { position: s.camera.position.toArray(), target: s.target.toArray(), fov: s.camera.fov },
            to: view, start: performance.now(), duration, resolve
        };
        s.dirty = true;
    });
}

function targetView(s) {
    return s.area ? zoneView(s.area, aspectOf(s)) : mainView(aspectOf(s));
}

// ---- Pasek stref (HTML) -------------------------------------------------------------------------------------------

// Pasek stref układa Blazor/CSS (pełny przy pierwszym wejściu, kompaktowy później, zawsze pełny na mobile).
// Scena podaje tylko rzut środków stref na ekran i kadr główny — diagnostyka i testy (klik w obiekt na canvas).
function placeZoneButtons(s) {
    const w = s.host.clientWidth || 1, h = s.host.clientHeight || 1;
    // Rzut z bieżącej pozycji kamery (koniec przejazdu jest liczony przed renderem, który dopiero odświeża macierze).
    s.camera.updateMatrixWorld();
    const points = {};
    for (const [zone, { hit: [, center] }] of Object.entries(ZONES)) {
        const p = new THREE.Vector3(...center).project(s.camera);
        points[zone] = [Math.round((p.x + 1) / 2 * w), Math.round((1 - p.y) / 2 * h)];
    }
    s.debug.points = points;
    s.debug.main = mainView(aspectOf(s)).position;
}

// ---- Wskazywanie i wybór stref na canvas (raycasting) --------------------------------------------------------------

function zoneAt(s, clientX, clientY) {
    const rect = s.renderer.domElement.getBoundingClientRect();
    s.pointer.set(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
    s.raycaster.setFromCamera(s.pointer, s.camera);
    return s.raycaster.intersectObjects(s.hits, false)[0]?.object.userData.zone ?? null;
}

function setHover(s, zone) {
    if (s.hover === zone) return;
    s.hover = zone;
    s.debug.hover = zone;
    if (s.zonesEl) {
        if (zone) s.zonesEl.dataset.hover = zone; else delete s.zonesEl.dataset.hover;
    }
    s.renderer.domElement.style.cursor = zone ? "pointer" : "";
    highlight(s);
}

function bindPointer(s) {
    const canvas = s.renderer.domElement;
    s.fine = !!(window.matchMedia && window.matchMedia("(hover: hover) and (pointer: fine)").matches);
    s.onMove = e => { if (!s.move) setHover(s, zoneAt(s, e.clientX, e.clientY)); };
    s.onLeave = () => setHover(s, null);
    s.onClick = e => {
        const zone = zoneAt(s, e.clientX, e.clientY);
        if (zone) s.dotnet?.invokeMethodAsync("SelectZone", zone).catch(() => { });
    };
    if (s.fine) {
        canvas.addEventListener("pointermove", s.onMove, { passive: true });
        canvas.addEventListener("pointerleave", s.onLeave, { passive: true });
    }
    canvas.addEventListener("click", s.onClick);

    // Fokus/hover na przycisku strefy (HTML) podświetla tę samą strefę w sali — klawiatura widzi to co mysz.
    if (s.zonesEl) {
        s.onZoneEnter = e => {
            const zone = e.target.closest?.("[data-zone]")?.dataset.zone;
            if (zone) setHover(s, zone);
        };
        s.onZoneExit = () => setHover(s, null);
        for (const type of ["focusin", "pointerover"]) s.zonesEl.addEventListener(type, s.onZoneEnter);
        for (const type of ["focusout", "pointerleave"]) s.zonesEl.addEventListener(type, s.onZoneExit);
    }
}

function unbindPointer(s) {
    const canvas = s.renderer.domElement;
    canvas.removeEventListener("pointermove", s.onMove);
    canvas.removeEventListener("pointerleave", s.onLeave);
    canvas.removeEventListener("click", s.onClick);
    if (s.zonesEl && s.onZoneEnter) {
        for (const type of ["focusin", "pointerover"]) s.zonesEl.removeEventListener(type, s.onZoneEnter);
        for (const type of ["focusout", "pointerleave"]) s.zonesEl.removeEventListener(type, s.onZoneExit);
        delete s.zonesEl.dataset.hover;
    }
}

// ---- Cykl życia ---------------------------------------------------------------------------------------------------

// Wynik: { renderer: "3d" | "css", reason } — przy "3d" pierwsza klatka sali jest już narysowana (pod warstwą Pending).
// options: { force (?scene=3d), known3d (przeglądarka wcześniej wybrała 3D — bez próby płynności),
//            zones (kontener przycisków stref), dotnet (SelectZone), arrived (gość przeszedł właśnie przez drzwi),
//            area (strefa przywracana bez animacji, np. otwarty bar po powrocie "wstecz") }.
export async function init(host, options) {
    const force = !!options?.force;
    dispose();
    const token = ++generation;
    const result = await startScene({
        force,
        known3d: !!options?.known3d,
        build: () => { setup(host, options ?? {}, force); return state; },
        isCurrent: () => token === generation,
        fail: dispose
    });
    window.__sansPostHallTimings = result.timings;
    if (result.renderer === "3d" && state) {
        state.debug.initMs = result.timings.total;
        // Wejście przez drzwi Entrance: z progu krótki settle do kadru głównego (reduced motion — od razu).
        if (options?.arrived && !options?.area) {
            moveTo(state, mainView(aspectOf(state)), 1100);
            placeZoneButtons(state);
        }
    }
    return { renderer: result.renderer, reason: result.reason };
}

function setup(host, options, force) {
    const renderer = createRenderer(host, force);
    try {
        const width = host.clientWidth || 1, height = host.clientHeight || 1;
        renderer.domElement.className = "hall-3d-canvas";
        host.appendChild(renderer.domElement);

        const built = performance.now();
        const world = buildScene(headingFont());
        // Wszystkie tekstury na GPU od razu (pod warstwą Pending) — inaczej trafiałyby tam dopiero przy pierwszym pojawieniu
        // się obiektu w kadrze, a pierwszy przejazd do strefy gubiłby klatki.
        world.scene.traverse(obj => {
            const list = Array.isArray(obj.material) ? obj.material : obj.material ? [obj.material] : [];
            for (const m of list) for (const t of [m.map, m.emissiveMap, m.bumpMap]) if (t) renderer.initTexture(t);
        });
        const camera = new THREE.PerspectiveCamera(55, width / height, 0.05, 60);

        state = {
            host, renderer, camera, ...world,
            zonesEl: options.zones ?? null,
            dotnet: options.dotnet ?? null,
            target: new THREE.Vector3(),
            raycaster: new THREE.Raycaster(),
            pointer: new THREE.Vector2(),
            area: options.area ?? null, hover: null, move: null, dirty: true,
            debug: { frames: 0, area: options.area ?? null, hover: null, moving: false, disposed: false,
                     buildMs: 0, initMs: 0, drawCalls: 0, triangles: 0, geometries: 0, textures: 0 }
        };
        window.__sansPostHall = state.debug;
        applyTheme(state, PALETTE[theme()]);
        // Wejście przez drzwi: pierwsza klatka już z progu (settle rusza po starcie), inaczej od razu kadr docelowy.
        const fromDoor = options.arrived && !options.area;
        setView(state, fromDoor ? { ...DOOR_VIEW, fov: mainView(aspectOf(state)).fov } : targetView(state));
        placeZoneButtons(state);
        state.debug.buildMs = Math.round(performance.now() - built);
        bindPointer(state);

        // ResizeObserver woła callback od razu po observe() — bez zmiany rozmiaru nic nie robimy (kadr progu zostaje).
        state.size = `${width}x${height}`;
        state.resize = new ResizeObserver(() => {
            const s = state;
            if (!s) return;
            const size = `${host.clientWidth || 1}x${host.clientHeight || 1}`;
            if (size === s.size) return;
            s.size = size;
            s.renderer.setSize(host.clientWidth || 1, host.clientHeight || 1, false);
            s.camera.aspect = aspectOf(s);
            if (s.move) s.move.to = targetView(s);
            else setView(s, targetView(s));
            placeZoneButtons(s);
        });
        state.resize.observe(host);

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
    if (!s.dirty && !s.move) return;
    s.dirty = false;

    if (s.move) {
        const { from, to, start, duration } = s.move;
        const t = Math.min(1, Math.max(0, (time - start) / duration));
        const e = easeInOutCubic(t);
        s.camera.position.set(...from.position.map((v, i) => v + (to.position[i] - v) * e));
        s.target.set(...from.target.map((v, i) => v + (to.target[i] - v) * e));
        s.camera.fov = from.fov + (to.fov - from.fov) * e;
        s.camera.updateProjectionMatrix();
        s.camera.lookAt(s.target);
        if (t >= 1) {
            const done = s.move.resolve;
            s.move = null;
            s.debug.moving = false;
            placeZoneButtons(s);
            done();
        }
    }

    s.renderer.render(s.scene, s.camera);
    const info = s.renderer.info;
    Object.assign(s.debug, {
        frames: s.debug.frames + 1,
        cameraX: s.camera.position.x, cameraY: s.camera.position.y, cameraZ: s.camera.position.z,
        drawCalls: info.render.calls, triangles: info.render.triangles,
        geometries: info.memory.geometries, textures: info.memory.textures
    });
}

// Kamera do strefy (bar/wanted/game/music). Przyciski stref przechodzą do paska u dołu na czas wyboru.
export function focusArea(zone) {
    const s = state;
    if (!s || !ZONES[zone]) return Promise.resolve();
    s.area = zone;
    s.debug.area = zone;
    highlight(s);
    const done = moveTo(s, zoneView(zone, aspectOf(s)), 950);
    placeZoneButtons(s);
    return done;
}

// Tablica Wanted w scenie: przydomki i tytuły z rankingu (posters: [{ alias, title }], pierwszy = Most Wanted; pusta
// lista — "Tablica czeka na pierwszą rozmowę."). Ta sama tekstura, przerysowana raz; jedna klatka renderu.
export function setWanted(posters) {
    const s = state;
    if (!s || !Array.isArray(posters)) return;
    s.zones.wanted.redraw(posters.slice(0, 1 + WANTED_SMALL.length).map(p => ({ alias: String(p.alias ?? ""), title: String(p.title ?? "") })));
    s.debug.wanted = posters.map(p => p.alias);
    s.dirty = true;
}

// Powrót do kadru głównego Main Hall.
export function resetView() {
    const s = state;
    if (!s) return Promise.resolve();
    s.area = null;
    s.debug.area = null;
    highlight(s);
    const done = moveTo(s, mainView(aspectOf(s)), 850);
    placeZoneButtons(s);
    return done;
}

// Opuszczenie /saloon: pętla, listenery, obserwatory, geometrie, materiały, tekstury, renderer, kontekst WebGL, canvas.
export function dispose() {
    generation++;
    const s = state;
    if (!s) return;
    state = null;
    s.move?.resolve();
    s.resize?.disconnect();
    s.themeObserver?.disconnect();
    unbindPointer(s);
    s.dotnet = null;
    disposeScene(s.scene, s.renderer);
    s.debug.disposed = true;
}
