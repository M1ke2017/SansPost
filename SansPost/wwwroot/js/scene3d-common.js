// SansPost — wspólne elementy scen Three.js (Entrance, Saloon Main Hall).
// Tylko to, co obie sceny naprawdę dzielą: decyzja renderera (jedna dla całej sesji), leniwe ładowanie Three.js,
// tekstury z canvas, scalanie statycznej geometrii i sprzątanie. Moduł nie importuje Three.js statycznie —
// urządzenia z fallbackiem CSS nigdy go nie pobierają.

export let THREE = null;

export async function loadThree() {
    THREE ??= await import("../lib/three/three.module.min.js");
    return THREE;
}

// ---- Decyzja: 3D czy CSS ------------------------------------------------------------------------------------------

const SOFTWARE_GL = /swiftshader|llvmpipe|softpipe|software|basic render driver|mesa offscreen/i;
const PROBE_KEY = "sp-scene-probe";   // wynik próby płynności w tej sesji: "ok" | "slow"
const HINT_COOKIE = "sp-scene";       // podpowiedź dla prerenderu kolejnych stron: "3d" | "css" (wyłącznie wygląd)

// Czysta funkcja: możliwości przeglądarki → renderer + powód.
// "force" (?scene=3d — diagnostyka i testy) pomija tylko kryteria wydajności, nigdy reduced motion ani brak WebGL.
export function chooseRenderer(caps) {
    if (caps.reducedMotion) return { renderer: "css", reason: "reduced-motion" };
    if (!caps.webgl) return { renderer: "css", reason: "no-webgl" };
    if (caps.force) return { renderer: "3d", reason: "forced" };
    if (caps.majorPerformanceCaveat) return { renderer: "css", reason: "performance-caveat" };
    if (SOFTWARE_GL.test(caps.rendererName || "")) return { renderer: "css", reason: "software-gl" };
    if (caps.saveData) return { renderer: "css", reason: "save-data" };
    if (caps.deviceMemory && caps.deviceMemory < 4) return { renderer: "css", reason: "low-memory" };
    if (caps.slowFrames) return { renderer: "css", reason: "slow-frames" };
    return { renderer: "3d", reason: "gpu" };
}

function probeContext(attributes) {
    const canvas = document.createElement("canvas");
    const gl = canvas.getContext("webgl2", attributes);
    if (!gl) return null;
    const info = gl.getExtension("WEBGL_debug_renderer_info");
    const name = String(info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER));
    gl.getExtension("WEBGL_lose_context")?.loseContext();
    return name;
}

function sessionValue(key, value) {
    try {
        if (value === undefined) return sessionStorage.getItem(key);
        sessionStorage.setItem(key, value);
    } catch { /* prywatny tryb / zablokowany storage */ }
    return null;
}

export function reducedMotion() {
    return !!(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches);
}

// Sonda WebGL (dwa krótkie konteksty) raz na załadowanie strony — warmUp i init korzystają z tego samego wyniku.
let gpu = null;

function probeGpu() {
    if (gpu) return gpu;
    gpu = { webgl: false, majorPerformanceCaveat: false, rendererName: "" };
    try {
        const fast = probeContext({ failIfMajorPerformanceCaveat: true });
        const any = fast ?? probeContext({});
        gpu = { webgl: any !== null, majorPerformanceCaveat: fast === null && any !== null, rendererName: any ?? "" };
    } catch {
        gpu.webgl = false;
    }
    return gpu;
}

export function capabilities(force) {
    const caps = { force, reducedMotion: reducedMotion(), webgl: false, majorPerformanceCaveat: false, rendererName: "",
                   deviceMemory: navigator.deviceMemory, saveData: !!navigator.connection?.saveData,
                   slowFrames: sessionValue(PROBE_KEY) === "slow" };
    return caps.reducedMotion ? caps : { ...caps, ...probeGpu() };
}

export function decideRenderer(force) {
    const decision = chooseRenderer(capabilities(force));
    window.__sansPost3dDecision = decision;
    return decision;
}

// Start jak najwcześniej (moduł z <head> strony "/", zanim podłączy się circuit Blazora): decyzja i — przy "3d" —
// pobieranie Three.js równolegle z blazor.server.js. init() dostaje potem gotowy moduł z pamięci przeglądarki.
export function warmUp(force) {
    const decision = decideRenderer(force);
    if (decision.renderer === "3d") loadThree().catch(() => { });
    return decision;
}

// Czy próba płynności jest jeszcze potrzebna: nie przy wymuszeniu, nie gdy przeglądarka już wcześniej wybrała 3D
// (ciasteczko "sp-scene=3d" — "known3d") i nie drugi raz w tej samej sesji.
export function needsFrameProbe(force, known3d = false) {
    return !force && !known3d && sessionValue(PROBE_KEY) !== "ok";
}

// Krótka próba płynności (~15 klatek, najwyżej 0,9 s) zamiast benchmarku. render() rysuje jedną klatkę sceny.
// Wołana dopiero po kompilacji shaderów i pierwszej klatce — mierzy płynność, nie zimny start GPU.
// Wynik: { ok, median } (median = mediana odstępów między klatkami w ms).
export function measureFrames(render) {
    return new Promise(resolve => {
        const times = [];
        const step = time => {
            try { render(); } catch { return resolve({ ok: false, median: Infinity }); }
            times.push(time);
            if (times.length < 16 && time - times[0] < 900) return requestAnimationFrame(step);
            // Pierwsze klatki kompilują shadery — liczą się odstępy między kolejnymi.
            const steady = times.slice(3);
            const gaps = steady.slice(1).map((t, i) => t - steady[i]).sort((a, b) => a - b);
            const median = gaps.length ? gaps[Math.floor(gaps.length / 2)] : Infinity;
            const ok = gaps.length >= 6 && median <= 34;
            sessionValue(PROBE_KEY, ok ? "ok" : "slow");
            resolve({ ok, median: Math.round(median * 10) / 10 });
        };
        requestAnimationFrame(step);
    });
}

// Podpowiedź dla serwera (prerender /saloon od razu w odpowiednim trybie, bez przeskoku układu).
// Tylko preferencja prezentacji — jak motyw — bez danych użytkownika. "3d" na 30 dni; "css" do końca sesji przeglądarki
// (np. chwilowy reduced motion czy tryb oszczędzania danych nie przypina fallbacku na miesiąc).
export function rememberRenderer(renderer) {
    document.cookie = renderer === "3d"
        ? `${HINT_COOKIE}=3d; path=/; max-age=2592000; samesite=lax`
        : `${HINT_COOKIE}=css; path=/; samesite=lax`;
}

export function theme() {
    return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
}

// Deterministyczny generator — stały układ detali (deski, notatki, gwiazdy).
export function random(seed) {
    return function () {
        seed |= 0; seed = seed + 0x6D2B79F5 | 0;
        let t = Math.imul(seed ^ seed >>> 15, 1 | seed);
        t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
        return ((t ^ t >>> 14) >>> 0) / 4294967296;
    };
}

export function clamp01(v) { return Math.min(1, Math.max(0, v)); }
export function phase(t, from, to) { return clamp01((t - from) / (to - from)); }
export function easeInOutCubic(x) { return x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2; }
export function easeOut(x) { return 1 - Math.pow(1 - x, 3); }

// ---- Start sceny --------------------------------------------------------------------------------------------------

// Jedna sekwencja startu dla każdej sceny 3D (Entrance, Main Hall): decyzja → Three.js → fonty → budowa → shadery →
// pierwsza klatka narysowana → (próba płynności przy pierwszym wyborze) → zapamiętanie wyboru.
// Wynik ("3d" | "css" + powód) zapada dopiero, gdy wiadomo, co pokazać: przy "3d" pierwsza klatka jest już w canvasie,
// więc strona może przejść z Pending prosto do sceny. Przy "css" (świadomy fallback) scena jest już posprzątana.
//   build()     — tworzy scenę i zwraca { renderer, scene, camera } (wołane dopiero po załadowaniu Three.js),
//   isCurrent() — false, gdy w międzyczasie strona odmontowała scenę (dispose),
//   fail()      — sprząta po nieudanym starcie albo zbyt wolnej próbie.
// timings (ms): decision, importThree, fonts, build, compile, firstFrame, probe, probeMedian, total.
export async function startScene({ force = false, known3d = false, build, isCurrent, fail }) {
    const timings = { decision: 0, importThree: 0, fonts: 0, build: 0, compile: 0, firstFrame: 0, probe: null, probeMedian: null, total: 0 };
    const started = performance.now();
    let mark = started;
    const lap = name => { const now = performance.now(); timings[name] = Math.round(now - mark); mark = now; };
    const finish = (renderer, reason) => {
        timings.total = Math.round(performance.now() - started);
        return { renderer, reason, timings };
    };

    const decision = decideRenderer(force);
    lap("decision");
    if (decision.renderer !== "3d") {
        rememberRenderer("css");
        return finish("css", decision.reason);
    }

    let view;
    try {
        await loadThree();                       // zwykle już w pamięci — warmUp z <head> zaczął pobieranie wcześniej
        lap("importThree");
        await Promise.race([document.fonts?.ready, new Promise(r => setTimeout(r, 800))]);
        lap("fonts");
        if (!isCurrent()) return finish("css", "cancelled");
        view = build();
        lap("build");
        await view.renderer.compileAsync(view.scene, view.camera);   // shadery przed pierwszą klatką (bez przycięcia)
        lap("compile");
        if (!isCurrent()) return finish("css", "cancelled");
        view.renderer.render(view.scene, view.camera);
        await new Promise(resolve => requestAnimationFrame(() => resolve()));   // klatka oddana do wyświetlenia
        lap("firstFrame");
    } catch (e) {
        console.warn("SansPost 3D: inicjalizacja nieudana, zostaje widok CSS/HTML.", e);
        fail();
        rememberRenderer("css");
        return finish("css", "init-failed");
    }

    // Krótka próba płynności, raz na sesję i tylko przy pierwszym wyborze: słaby GPU → CSS bez blokowania strony.
    if (needsFrameProbe(force, known3d)) {
        const result = await measureFrames(() => view.renderer.render(view.scene, view.camera));
        lap("probe");
        timings.probeMedian = result.median;
        if (!isCurrent()) return finish("css", "cancelled");
        if (!result.ok) {
            fail();
            rememberRenderer("css");
            return finish("css", "slow-frames");
        }
    }
    if (!isCurrent()) return finish("css", "cancelled");
    rememberRenderer("3d");
    return finish("3d", decision.reason);
}

// ---- Renderer -----------------------------------------------------------------------------------------------------

export function createRenderer(host, force) {
    const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance", failIfMajorPerformanceCaveat: !force });
    const width = host.clientWidth || 1, height = host.clientHeight || 1;
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, width * height > 2_000_000 ? 1.5 : 2));
    renderer.setSize(width, height, false);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.shadowMap.autoUpdate = false;   // statyczne światła i geometria — mapa cieni tylko przy starcie i zmianie motywu
    renderer.domElement.setAttribute("aria-hidden", "true");
    return renderer;
}

// Zwolnienie wszystkiego, co scena trzyma na GPU: geometrie, materiały, tekstury, renderer, kontekst WebGL, canvas.
export function disposeScene(scene, renderer) {
    scene.traverse(obj => {
        obj.geometry?.dispose();
        obj.dispose?.();                       // InstancedMesh (bufory instancji)
        const list = Array.isArray(obj.material) ? obj.material : obj.material ? [obj.material] : [];
        for (const m of list) {
            m.map?.dispose();
            m.emissiveMap?.dispose();
            m.dispose();
        }
    });
    renderer.setAnimationLoop(null);
    renderer.dispose();
    renderer.forceContextLoss();
    renderer.domElement.remove();
}

// ---- Tekstury z canvas (bez plików graficznych) -------------------------------------------------------------------

export function canvasTexture(width, height, draw, repeat = true) {
    const canvas = document.createElement("canvas");
    canvas.width = width; canvas.height = height;
    draw(canvas.getContext("2d"), width, height);
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = 4;
    if (repeat) texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
    return texture;
}

// Deski w odcieniach szarości (kolor nadaje materiał): pionowe (fasady) albo poziome (siding, podłogi z przesuniętymi łączeniami).
export function boardsTexture(seed, { boards = 8, horizontal = false, staggered = false } = {}) {
    const rnd = random(seed);
    return canvasTexture(256, 256, (ctx, w) => {
        const size = w / boards;
        const rect = (along, across, length, thickness) =>
            horizontal ? ctx.fillRect(along, across, length, thickness) : ctx.fillRect(across, along, thickness, length);
        for (let i = 0; i < boards; i++) {
            const tone = 196 + Math.round((rnd() - 0.5) * 44);
            ctx.fillStyle = `rgb(${tone},${tone},${tone})`;
            rect(0, i * size, w, size);
            if (staggered) {
                // Łączenia desek podłogi w różnych miejscach w kolejnych rzędach.
                const joint = ((i * 0.37) % 1) * w;
                const t = tone + Math.round((rnd() - 0.5) * 18);
                ctx.fillStyle = `rgb(${t},${t},${t})`;
                rect(joint, i * size, w - joint, size);
                ctx.fillStyle = "rgba(24,14,7,0.6)";
                rect(joint, i * size, 2, size);
            }
            // Słoje: cienkie, lekko falujące linie wzdłuż deski.
            for (let g = 0; g < 7; g++) {
                const offset = rnd() * size;
                ctx.strokeStyle = `rgba(40,24,12,${0.05 + rnd() * 0.08})`;
                ctx.lineWidth = 1;
                ctx.beginPath();
                for (let p = 0; p <= 16; p++) {
                    const a = p / 16 * w;
                    const b = i * size + offset + Math.sin(p * 0.9 + g) * 1.4;
                    if (horizontal) ctx.lineTo(a, b); else ctx.lineTo(b, a);
                }
                ctx.stroke();
            }
            // Fuga między deskami.
            ctx.fillStyle = "rgba(24,14,7,0.6)";
            rect(0, i * size, w, 2);
        }
    });
}

export function noiseTexture(seed, base, spots, ruts = false) {
    const rnd = random(seed);
    return canvasTexture(256, 256, (ctx, w, h) => {
        ctx.fillStyle = `rgb(${base},${base},${base})`;
        ctx.fillRect(0, 0, w, h);
        for (let i = 0; i < spots; i++) {
            const t = base + Math.round((rnd() - 0.5) * 70);
            ctx.fillStyle = `rgba(${t},${t},${t},${0.25 + rnd() * 0.35})`;
            ctx.beginPath(); ctx.arc(rnd() * w, rnd() * h, 0.6 + rnd() * 2.2, 0, Math.PI * 2); ctx.fill();
        }
        if (ruts) {
            // Koleiny wozów wzdłuż ulicy.
            for (const x of [0.3, 0.37, 0.63, 0.7]) {
                const gradient = ctx.createLinearGradient((x - 0.03) * w, 0, (x + 0.03) * w, 0);
                gradient.addColorStop(0, "rgba(60,40,24,0)");
                gradient.addColorStop(0.5, "rgba(60,40,24,0.28)");
                gradient.addColorStop(1, "rgba(60,40,24,0)");
                ctx.fillStyle = gradient;
                ctx.fillRect((x - 0.03) * w, 0, 0.06 * w, h);
            }
        }
    });
}

export function wallpaperTexture() {
    return canvasTexture(128, 128, (ctx, w, h) => {
        ctx.fillStyle = "rgb(200,200,200)";
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = "rgba(90,60,40,0.35)";
        for (let x = 0; x < w; x += 32) ctx.fillRect(x, 0, 6, h);
        ctx.fillStyle = "rgba(255,230,180,0.3)";
        for (let x = 16; x < w; x += 32) for (let y = 12; y < h; y += 32) {
            ctx.beginPath(); ctx.moveTo(x, y - 5); ctx.lineTo(x + 4, y); ctx.lineTo(x, y + 5); ctx.lineTo(x - 4, y); ctx.fill();
        }
    });
}

// Malowany szyld (tekst na desce). Font nagłówków strony — po document.fonts.ready.
export function signTexture(text, { width = 512, height = 128, kicker = null, bg = "#2b190e", fg = "#f4e3c1", border = "#b88a4c", font = "serif" } = {}) {
    return canvasTexture(width, height, (ctx, w, h) => {
        ctx.fillStyle = bg;
        ctx.fillRect(0, 0, w, h);
        ctx.strokeStyle = border;
        ctx.lineWidth = Math.max(4, h * 0.05);
        ctx.strokeRect(h * 0.08, h * 0.08, w - h * 0.16, h - h * 0.16);
        ctx.lineWidth = 1.5;
        ctx.strokeRect(h * 0.15, h * 0.15, w - h * 0.3, h - h * 0.3);
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.shadowColor = "rgba(0,0,0,0.45)";
        ctx.shadowOffsetY = h * 0.02;
        if (kicker) {
            ctx.fillStyle = border;
            ctx.font = `700 ${Math.round(h * 0.15)}px ${font}`;
            ctx.fillText(kicker.toUpperCase().split("").join(" "), w / 2, h * 0.3);
            ctx.fillStyle = fg;
            ctx.font = `700 ${Math.round(h * 0.42)}px ${font}`;
            ctx.fillText(text, w / 2, h * 0.63);
        } else {
            ctx.fillStyle = fg;
            let size = h * 0.5;
            ctx.font = `700 ${Math.round(size)}px ${font}`;
            while (ctx.measureText(text).width > w * 0.8 && size > 10) ctx.font = `700 ${Math.round(size -= 2)}px ${font}`;
            ctx.fillText(text, w / 2, h * 0.54);
        }
    }, false);
}

// Płaski szyld podświetlany własną teksturą (emissiveMap) — czytelny także nocą; siłę ustawia motyw sceny.
export function signMesh(texture, w, h, kind = "town") {
    const material = new THREE.MeshStandardMaterial({ map: texture, emissive: "#ffffff", emissiveMap: texture, emissiveIntensity: 0, roughness: 0.7 });
    material.userData.sign = kind;
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, h), material);
    mesh.receiveShadow = true;
    return mesh;
}

export function headingFont() {
    return getComputedStyle(document.querySelector("h1") || document.body).fontFamily || "serif";
}

// ---- Batch: statyczna geometria scalona per materiał (kilkadziesiąt draw calli zamiast setek) ----------------------

export class Batch {
    constructor() {
        this.groups = new Map();
        this.frame = new THREE.Matrix4();
    }

    // Dodaje geometrię w lokalnym układzie bieżącej ramki (frame): obrót XYZ + przesunięcie.
    add(material, geometry, x = 0, y = 0, z = 0, rx = 0, ry = 0, rz = 0) {
        const local = new THREE.Matrix4().makeRotationFromEuler(new THREE.Euler(rx, ry, rz));
        local.setPosition(x, y, z);
        geometry.applyMatrix4(new THREE.Matrix4().multiplyMatrices(this.frame, local));
        worldUv(geometry, material.userData.tile ?? 2);
        if (!this.groups.has(material)) this.groups.set(material, []);
        this.groups.get(material).push(geometry);
    }

    box(material, w, h, d, x, y, z, rx = 0, ry = 0, rz = 0) {
        this.add(material, new THREE.BoxGeometry(w, h, d), x, y, z, rx, ry, rz);
    }

    // Pojedynczy obiekt (szyld) w układzie ramki.
    place(object, x, y, z) {
        object.position.set(x, y, z);
        object.updateMatrix();
        object.applyMatrix4(this.frame);
        return object;
    }

    build(parent) {
        for (const [material, list] of this.groups) {
            const mesh = new THREE.Mesh(mergeGeometries(list), material);
            mesh.castShadow = true;
            mesh.receiveShadow = true;
            parent.add(mesh);
            list.forEach(g => g.dispose());
        }
        this.groups.clear();
    }
}

// UV ze współrzędnych świata (rzut na dominującą oś normalnej): gęstość desek i cegieł niezależna od rozmiaru bryły.
export function worldUv(geometry, tile) {
    const p = geometry.attributes.position, n = geometry.attributes.normal, uv = geometry.attributes.uv;
    for (let i = 0; i < p.count; i++) {
        const ax = Math.abs(n.getX(i)), ay = Math.abs(n.getY(i)), az = Math.abs(n.getZ(i));
        let u, v;
        if (ax >= ay && ax >= az) { u = p.getZ(i); v = p.getY(i); }
        else if (ay >= az) { u = p.getX(i); v = p.getZ(i); }
        else { u = p.getX(i); v = p.getY(i); }
        uv.setXY(i, u / tile, v / tile);
    }
}

export function mergeGeometries(list) {
    let vertices = 0, indices = 0;
    for (const g of list) {
        vertices += g.attributes.position.count;
        indices += g.index ? g.index.count : g.attributes.position.count;
    }
    const position = new Float32Array(vertices * 3), normal = new Float32Array(vertices * 3), uv = new Float32Array(vertices * 2);
    const index = new Uint32Array(indices);
    let vo = 0, io = 0;
    for (const g of list) {
        const count = g.attributes.position.count;
        position.set(g.attributes.position.array, vo * 3);
        normal.set(g.attributes.normal.array, vo * 3);
        uv.set(g.attributes.uv.array, vo * 2);
        if (g.index) for (let i = 0; i < g.index.count; i++) index[io++] = g.index.array[i] + vo;
        else for (let i = 0; i < count; i++) index[io++] = vo + i;
        vo += count;
    }
    const merged = new THREE.BufferGeometry();
    merged.setAttribute("position", new THREE.BufferAttribute(position, 3));
    merged.setAttribute("normal", new THREE.BufferAttribute(normal, 3));
    merged.setAttribute("uv", new THREE.BufferAttribute(uv, 2));
    merged.setIndex(new THREE.BufferAttribute(index, 1));
    merged.computeBoundingSphere();
    return merged;
}
