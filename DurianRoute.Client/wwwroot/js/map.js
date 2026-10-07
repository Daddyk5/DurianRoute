// Leaflet bridge for the Blazor pages. Each map is keyed by its container element id.
window.durianMap = (function () {
    const DAVAO = [7.085, 125.59];
    const maps = {};

    function congestionColor(vc) {
        if (vc >= 1.0) return "#c62828";
        if (vc >= 0.85) return "#ef6c00";
        if (vc >= 0.65) return "#f9a825";
        return "#2e7d32";
    }

    function busIcon(bus, color) {
        const late = bus.deviationMinutes > 5 ? " late" : bus.deviationMinutes < -4 ? " early" : "";
        const held = bus.status === 3 ? " held" : "";
        return L.divIcon({
            className: "bus-marker-wrap",
            iconSize: [26, 26],
            iconAnchor: [13, 13],
            html: `<div class="bus-marker${late}${held}" style="background:${color}">
                     <div class="bus-arrow" style="transform:rotate(${bus.headingDegrees}deg)">&#9650;</div>
                   </div><div class="bus-label">${bus.plateNumber}</div>`
        });
    }

    function init(elementId, dotnetRef) {
        dispose(elementId);
        const map = L.map(elementId, { zoomControl: true }).setView(DAVAO, 13);
        L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
            maxZoom: 19,
            attribution: "&copy; OpenStreetMap contributors"
        }).addTo(map);

        maps[elementId] = {
            map, dotnetRef,
            routes: {}, routeColors: {}, buses: {}, chokes: {}, forecast: L.layerGroup().addTo(map),
            hiddenRoutes: new Set()
        };
        setTimeout(() => map.invalidateSize(), 200);
    }

    function setRoutes(elementId, routes) {
        const m = maps[elementId];
        if (!m) return;
        Object.values(m.routes).forEach(layer => layer.remove());
        m.routes = {};
        const bounds = [];
        for (const r of routes) {
            m.routeColors[r.id] = r.color;
            const group = L.layerGroup();
            const latlngs = r.path.map(p => [p.lat, p.lng]);
            bounds.push(...latlngs);
            L.polyline(latlngs, { color: r.color, weight: 5, opacity: 0.7 })
                .bindTooltip(`${r.code} · ${r.name}`).addTo(group);
            for (const s of r.stops) {
                L.circleMarker([s.lat, s.lng], { radius: 4, color: r.color, fillColor: "#fff", fillOpacity: 1, weight: 2 })
                    .bindTooltip(s.name).addTo(group);
            }
            if (!m.hiddenRoutes.has(r.id)) group.addTo(m.map);
            m.routes[r.id] = group;
        }
        if (bounds.length) m.map.fitBounds(bounds, { padding: [30, 30] });
    }

    function setRouteVisible(elementId, routeId, visible) {
        const m = maps[elementId];
        if (!m) return;
        visible ? m.hiddenRoutes.delete(routeId) : m.hiddenRoutes.add(routeId);
        const layer = m.routes[routeId];
        if (layer) visible ? layer.addTo(m.map) : layer.remove();
        for (const [id, entry] of Object.entries(m.buses)) {
            if (entry.routeId === routeId) visible ? entry.marker.addTo(m.map) : entry.marker.remove();
        }
    }

    function updateBuses(elementId, positions) {
        const m = maps[elementId];
        if (!m) return;
        for (const b of positions) {
            const color = m.routeColors[b.routeId] || "#1e88e5";
            let entry = m.buses[b.busId];
            if (!entry) {
                const marker = L.marker([b.lat, b.lng], { icon: busIcon(b, color), zIndexOffset: 1000 });
                marker.on("click", () => m.dotnetRef && m.dotnetRef.invokeMethodAsync("OnBusSelected", b.busId));
                entry = m.buses[b.busId] = { marker, routeId: b.routeId };
                if (!m.hiddenRoutes.has(b.routeId)) marker.addTo(m.map);
            }
            entry.marker.setLatLng([b.lat, b.lng]);
            entry.marker.setIcon(busIcon(b, color));
        }
    }

    function setChokePoints(elementId, chokePoints) {
        const m = maps[elementId];
        if (!m) return;
        Object.values(m.chokes).forEach(c => c.remove());
        m.chokes = {};
        for (const c of chokePoints) {
            m.chokes[c.id] = L.circle([c.lat, c.lng], { radius: 220, color: "#555", weight: 2, fillOpacity: 0.35 })
                .bindTooltip(c.name).addTo(m.map);
        }
    }

    function updateChokeStatus(elementId, statuses) {
        const m = maps[elementId];
        if (!m) return;
        for (const s of statuses) {
            const circle = m.chokes[s.chokePointId];
            if (!circle) continue;
            const vc = Math.max(s.inboundVc, s.outboundVc);
            const color = congestionColor(vc);
            circle.setStyle({ color: s.incidentActive ? "#000" : color, fillColor: color, dashArray: s.incidentActive ? "6 4" : null });
            circle.setTooltipContent(
                `<b>${s.name}</b>${s.incidentActive ? " ⚠ incident" : ""}<br/>` +
                `In: ${s.inboundVolume} veh/h (v/c ${s.inboundVc}, +${s.inboundDelayMinutes} min)<br/>` +
                `Out: ${s.outboundVolume} veh/h (v/c ${s.outboundVc}, +${s.outboundDelayMinutes} min)`);
        }
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

    return { init, setRoutes, setRouteVisible, updateBuses, setChokePoints, updateChokeStatus, showForecast, dispose };
})();

window.durianStorage = {
    get: key => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { } },
    remove: key => { try { localStorage.removeItem(key); } catch { } }
};
