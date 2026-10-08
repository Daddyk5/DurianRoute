// Leaflet bridge for the Blazor pages. Each map is keyed by its container element id.
window.durianMap = (function () {
    const DAVAO = [7.085, 125.59];
    const CONGESTION_RADIUS_M = 650;
    // Esri canvas basemaps: muted streets so routes and congestion stand out. No API key required.
    const ESRI = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/";
    const TILES = {
        light: { base: ESRI + "World_Light_Gray_Base/MapServer/tile/{z}/{y}/{x}", labels: ESRI + "World_Light_Gray_Reference/MapServer/tile/{z}/{y}/{x}" },
        dark: { base: ESRI + "World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}", labels: ESRI + "World_Dark_Gray_Reference/MapServer/tile/{z}/{y}/{x}" }
    };
    const TILE_OPTIONS = { maxZoom: 19, maxNativeZoom: 16, attribution: "Tiles &copy; Esri &mdash; Esri, HERE, Garmin, &copy; OpenStreetMap contributors" };
    const maps = {};

    function congestionColor(vc) {
        if (vc >= 1.0) return "#DC2626";
        if (vc >= 0.85) return "#EA580C";
        if (vc >= 0.65) return "#EAB308";
        return "#16A34A";
    }

    function distanceM(a, b) {
        const R = 6371000, rad = Math.PI / 180;
        const dLat = (b[0] - a[0]) * rad, dLng = (b[1] - a[1]) * rad;
        const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * rad) * Math.cos(b[0] * rad) * Math.sin(dLng / 2) ** 2;
        return 2 * R * Math.asin(Math.sqrt(h));
    }

    function init(elementId, dotnetRef, options) {
        dispose(elementId);
        const dark = !!(options && options.dark);
        const map = L.map(elementId, { zoomControl: false, attributionControl: true }).setView(DAVAO, 13);
        L.control.zoom({ position: "bottomright" }).addTo(map);

        const labelsPane = map.createPane("labels");
        labelsPane.style.zIndex = 380;
        labelsPane.style.pointerEvents = "none";
        map.createPane("congestion").style.zIndex = 390;
        map.createPane("routes").style.zIndex = 400;
        map.createPane("stops").style.zIndex = 410;
        map.createPane("chokes").style.zIndex = 420;

        const set = dark ? TILES.dark : TILES.light;
        const tiles = {
            base: L.tileLayer(set.base, TILE_OPTIONS).addTo(map),
            labels: L.tileLayer(set.labels, { ...TILE_OPTIONS, pane: "labels", attribution: "" }).addTo(map)
        };

        const m = maps[elementId] = {
            map, dotnetRef, tiles,
            routes: {}, routeColors: {}, routePaths: {}, hiddenRoutes: new Set(),
            stops: L.layerGroup().addTo(map),
            congestion: L.layerGroup().addTo(map),
            chokeLayer: L.layerGroup().addTo(map),
            congestionSegs: {}, chokes: {}, chokeData: [],
            buses: {}, selected: null,
            forecast: L.layerGroup().addTo(map)
        };

        // Buses glide between 1-second updates; switch the glide off while zooming.
        map.on("zoomstart", () => map.getContainer().classList.add("no-glide"));
        map.on("zoomend", () => setTimeout(() => map.getContainer().classList.remove("no-glide"), 60));
        const updateLabels = () => map.getContainer().classList.toggle("labels-off", map.getZoom() < 14);
        map.on("zoomend", updateLabels);
        updateLabels();
        setTimeout(() => map.invalidateSize(), 200);
        return m;
    }

    function setTheme(elementId, dark) {
        const m = maps[elementId];
        if (!m) return;
        const set = dark ? TILES.dark : TILES.light;
        m.tiles.base.setUrl(set.base);
        m.tiles.labels.setUrl(set.labels);
    }

    function setRoutes(elementId, routes) {
        const m = maps[elementId];
        if (!m) return;
        Object.values(m.routes).forEach(layer => layer.remove());
        m.routes = {};
        m.stops.clearLayers();
        const bounds = [];
        for (const r of routes) {
            m.routeColors[r.id] = r.color;
            const latlngs = r.path.map(p => [p.lat, p.lng]);
            m.routePaths[r.id] = latlngs;
            bounds.push(...latlngs);

            const group = L.layerGroup();
            L.polyline(latlngs, { pane: "routes", color: "#FFFFFF", weight: 8, opacity: 0.9, lineJoin: "round" }).addTo(group);
            L.polyline(latlngs, { pane: "routes", color: r.color, weight: 4.5, opacity: 0.95, lineJoin: "round" })
                .bindTooltip(`<b>${r.code}</b> · ${r.name}`, { sticky: true }).addTo(group);
            if (!m.hiddenRoutes.has(r.id)) group.addTo(m.map);
            m.routes[r.id] = group;

            for (const s of r.stops) {
                L.circleMarker([s.lat, s.lng], { pane: "stops", radius: 4.5, color: r.color, weight: 2.5, fillColor: "#FFFFFF", fillOpacity: 1 })
                    .bindTooltip(`${s.name}<br><small>${r.code}</small>`).addTo(m.stops);
            }
        }
        if (bounds.length) m.map.fitBounds(bounds, { padding: [30, 30] });
        buildCongestion(m);
    }

    function setChokePoints(elementId, chokePoints) {
        const m = maps[elementId];
        if (!m) return;
        m.chokeData = chokePoints;
        m.chokeLayer.clearLayers();
        m.chokes = {};
        for (const c of chokePoints) {
            const marker = L.marker([c.lat, c.lng], {
                pane: "chokes", title: c.name,
                icon: L.divIcon({ className: "choke-wrap", iconSize: null, html: `<div class="choke-badge"><span class="choke-dot"></span><span class="choke-name">${c.name}</span></div>` })
            }).addTo(m.chokeLayer);
            m.chokes[c.id] = marker;
        }
        buildCongestion(m);
    }

    // Road segments within CONGESTION_RADIUS_M of each choke point, taken from the route geometry.
    function buildCongestion(m) {
        m.congestion.clearLayers();
        m.congestionSegs = {};
        for (const c of m.chokeData) {
            const center = [c.lat, c.lng];
            const segs = [];
            for (const path of Object.values(m.routePaths)) {
                let run = [];
                for (const p of path) {
                    if (distanceM(p, center) <= CONGESTION_RADIUS_M) run.push(p);
                    else { if (run.length > 1) segs.push(run); run = []; }
                }
                if (run.length > 1) segs.push(run);
            }
            m.congestionSegs[c.id] = segs.map(s =>
                L.polyline(s, { pane: "congestion", color: "#16A34A", weight: 16, opacity: 0.45, lineCap: "round", lineJoin: "round" }).addTo(m.congestion));
        }
    }

    function updateChokeStatus(elementId, statuses) {
        const m = maps[elementId];
        if (!m) return;
        for (const s of statuses) {
            const vc = Math.max(s.inboundVc, s.outboundVc);
            const color = congestionColor(vc);
            for (const seg of m.congestionSegs[s.chokePointId] || [])
                seg.setStyle({ color, opacity: vc >= 0.85 ? 0.6 : 0.42, dashArray: s.incidentActive ? "2 14" : null });

            const marker = m.chokes[s.chokePointId];
            if (!marker) continue;
            const el = marker.getElement();
            if (el) {
                const badge = el.querySelector(".choke-badge");
                badge.style.setProperty("--choke", color);
                badge.classList.toggle("incident", !!s.incidentActive);
            }
            marker.unbindTooltip().bindTooltip(
                `<b>${s.name}</b>${s.incidentActive ? " · ⚠ incident" : ""}<br>` +
                `Inbound ${s.inboundVolume} veh/h · v/c ${s.inboundVc} · +${s.inboundDelayMinutes} min<br>` +
                `Outbound ${s.outboundVolume} veh/h · v/c ${s.outboundVc} · +${s.outboundDelayMinutes} min` +
                (s.capacityFactor < 1 ? `<br>Capacity ${Math.round(s.capacityFactor * 100)}% (weather/incident)` : ""));
        }
    }

    function busClasses(b, selected) {
        const cls = ["bus-marker"];
        if (b.deviationMinutes > 5) cls.push("late");
        else if (b.deviationMinutes < -4) cls.push("early");
        if (b.status === 3) cls.push("held");
        if (b.headwayMinutes != null && b.headwayMinutes < 3) cls.push("bunch");
        if (selected) cls.push("selected");
        return cls.join(" ");
    }

    function updateBuses(elementId, positions) {
        const m = maps[elementId];
        if (!m) return;
        for (const b of positions) {
            const color = m.routeColors[b.routeId] || "#2563EB";
            let entry = m.buses[b.busId];
            if (!entry) {
                const icon = L.divIcon({
                    className: "bus-marker-wrap", iconSize: [28, 28], iconAnchor: [14, 14],
                    html: `<div class="${busClasses(b, false)}" style="--route:${color}"><div class="bus-arrow">&#9650;</div></div><div class="bus-label">${b.plateNumber}</div>`
                });
                const marker = L.marker([b.lat, b.lng], { icon, zIndexOffset: 1000, keyboard: true, title: b.plateNumber });
                marker.on("click", () => m.dotnetRef && m.dotnetRef.invokeMethodAsync("OnBusSelected", b.busId));
                entry = m.buses[b.busId] = { marker, routeId: b.routeId };
                if (!m.hiddenRoutes.has(b.routeId)) marker.addTo(m.map);
            }
            entry.last = b;
            entry.marker.setLatLng([b.lat, b.lng]);
            const el = entry.marker.getElement();
            if (el) {
                el.querySelector(".bus-marker").className = busClasses(b, m.selected === b.busId);
                el.querySelector(".bus-arrow").style.transform = `rotate(${b.headingDegrees}deg)`;
            }
        }
    }

    function selectBus(elementId, busId) {
        const m = maps[elementId];
        if (!m) return;
        m.selected = busId;
        for (const [id, entry] of Object.entries(m.buses)) {
            const el = entry.marker.getElement();
            if (el) el.querySelector(".bus-marker").classList.toggle("selected", Number(id) === busId);
        }
    }

    function flyToBus(elementId, busId) {
        const m = maps[elementId];
        const entry = m && m.buses[busId];
        if (entry) m.map.flyTo(entry.marker.getLatLng(), Math.max(m.map.getZoom(), 15), { duration: 0.8 });
    }

    function setRouteVisible(elementId, routeId, visible) {
        const m = maps[elementId];
        if (!m) return;
        visible ? m.hiddenRoutes.delete(routeId) : m.hiddenRoutes.add(routeId);
        const layer = m.routes[routeId];
        if (layer) visible ? layer.addTo(m.map) : layer.remove();
        for (const entry of Object.values(m.buses)) {
            if (entry.routeId === routeId) visible ? entry.marker.addTo(m.map) : entry.marker.remove();
        }
    }

    function setLayerVisible(elementId, name, visible) {
        const m = maps[elementId];
        if (!m) return;
        const layer = { congestion: m.congestion, stops: m.stops, chokes: m.chokeLayer }[name];
        if (layer) visible ? layer.addTo(m.map) : layer.remove();
    }

    // Predictive heatmap layer: one circle per choke point sized and colored by forecast v/c.
    function showForecast(elementId, points) {
        const m = maps[elementId];
        if (!m) return;
        m.forecast.clearLayers();
        const bounds = [];
        for (const p of points) {
            bounds.push([p.lat, p.lng]);
            L.circle([p.lat, p.lng], {
                radius: 250 + 450 * Math.min(p.vc, 1.6),
                color: congestionColor(p.vc), fillColor: congestionColor(p.vc), fillOpacity: 0.45, weight: 1
            }).bindTooltip(`<b>${p.name}</b><br/>v/c ${p.vc.toFixed(2)} · +${p.delay} min`).addTo(m.forecast);
        }
        if (bounds.length && !m.fitted) { m.map.fitBounds(bounds, { padding: [40, 40] }); m.fitted = true; }
    }

    function dispose(elementId) {
        const m = maps[elementId];
        if (!m) return;
        m.map.remove();
        delete maps[elementId];
    }

    return { init, setTheme, setRoutes, setChokePoints, updateChokeStatus, updateBuses, selectBus, flyToBus, setRouteVisible, setLayerVisible, showForecast, dispose };
})();

window.durianStorage = {
    get: key => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { } },
    remove: key => { try { localStorage.removeItem(key); } catch { } }
};
