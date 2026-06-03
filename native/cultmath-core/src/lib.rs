#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CultMathColor32 {
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

#[no_mangle]
pub unsafe extern "C" fn cultmath_apollonian_voronoi_tones(
    xs: *const f32,
    ys: *const f32,
    tones: *const u8,
    spans: *const f32,
    count: usize,
    resolution_y: f32,
    frame_index: i32,
    out_colors: *mut CultMathColor32,
) -> i32 {
    if xs.is_null() || ys.is_null() || tones.is_null() || spans.is_null() || out_colors.is_null() {
        return -1;
    }

    let xs = unsafe { std::slice::from_raw_parts(xs, count) };
    let ys = unsafe { std::slice::from_raw_parts(ys, count) };
    let tones = unsafe { std::slice::from_raw_parts(tones, count) };
    let spans = unsafe { std::slice::from_raw_parts(spans, count) };
    let out_colors = unsafe { std::slice::from_raw_parts_mut(out_colors, count) };
    for index in 0..count {
        out_colors[index] = tone_color(
            xs[index],
            ys[index],
            resolution_y,
            frame_index,
            tones[index],
            spans[index],
        );
    }

    0
}

fn tone_color(px: f32, py: f32, resolution_y: f32, frame_index: i32, tone: u8, span: f32) -> CultMathColor32 {
    let sample = stable_sample(px, py, resolution_y, frame_index, tone, span);
    let luminance = saturate(sample.0 * 0.299 + sample.1 * 0.587 + sample.2 * 0.114);
    match tone {
        3 => {
            let glow = 0.62 + luminance * 0.38;
            color(255.0 * glow, 96.0 * glow, 14.0 * glow)
        }
        4 => {
            let glow = 0.68 + luminance * 0.32;
            color(245.0 * glow, 248.0 * glow, 252.0 * glow)
        }
        _ => {
            let (gain, lift) = match tone {
                1 => (0.25, 6.0),
                2 => (0.95, 20.0),
                _ => (0.16, 2.0),
            };
            color(
                lift + sample.0 * 255.0 * gain,
                lift + sample.1 * 255.0 * gain,
                lift + sample.2 * 255.0 * gain,
            )
        }
    }
}

fn stable_sample(px: f32, py: f32, resolution_y: f32, frame_index: i32, tone: u8, span: f32) -> (f32, f32, f32) {
    let seed = px * 12.9898 + py * 78.233 + hash1((tone as f32 + 1.0) * 19.19);
    let jitter_x = (hash1(seed) - 0.5) * span;
    let jitter_y = (hash1(seed + 37.17) - 0.5) * span;
    voronoi(px + jitter_x, py + jitter_y, resolution_y, frame_index)
}

fn voronoi(px: f32, py: f32, resolution_y: f32, frame_index: i32) -> (f32, f32, f32) {
    let time = frame_index as f32 / 120.0;
    let scale = 6.0;
    let x = scale * px / resolution_y.max(1.0);
    let y = scale * py / resolution_y.max(1.0);
    let nx = x.floor();
    let ny = y.floor();
    let fx = frac(x);
    let fy = frac(y);
    let mut distance = 8.0;
    let mut red = 0.0;
    let mut green = 0.0;
    let mut blue = 0.0;
    let smoothness = 0.005;

    for j in -2..=2 {
        for i in -2..=2 {
            let gx = i as f32;
            let gy = j as f32;
            let (mut ox, mut oy) = hash2(nx + gx, ny + gy);
            let weight = ox * 0.5 + 0.5;
            ox = 0.5 + 0.5 * (time + 6.2831 * ox).sin();
            oy = 0.5 + 0.5 * (time + 6.2831 * oy).sin();
            let dx = (gx - fx + ox).abs();
            let dy = (gy - fy + oy).abs();
            let d = dx.max(dy) * weight;
            let seed = hash1((nx + gx) * 7.0 + (ny + gy) * 113.0);
            let candidate_red = 0.5 + 0.5 * (seed * 2.5 + 3.5 + 2.0).sin();
            let candidate_green = 0.5 + 0.5 * (seed * 2.5 + 3.5 + 3.0).sin();
            let candidate_blue = 0.5 + 0.5 * (seed * 2.5 + 3.5 + 2.0).sin();
            let h = smoothstep(0.0, 1.0, 0.5 + 0.5 * (distance - d) / smoothness);
            let correction = h * (1.0 - h) * smoothness / (1.0 + 3.0 * smoothness);
            distance = lerp(distance, d, h) - correction;
            red = lerp(red, candidate_red, h) - correction;
            green = lerp(green, candidate_green, h) - correction;
            blue = lerp(blue, candidate_blue, h) - correction;
        }
    }

    let edge_dimming = 1.0 - 0.1 * smoothstep(0.04, 0.05, distance);
    (
        (red * edge_dimming).max(0.0),
        (green * edge_dimming).max(0.0),
        (blue * edge_dimming).max(0.0),
    )
}

fn hash1(value: f32) -> f32 {
    frac(value.sin() * 43758.5453)
}

fn hash2(x: f32, y: f32) -> (f32, f32) {
    (
        frac((x * 127.1 + y * 311.7).sin() * 43758.5453),
        frac((x * 269.5 + y * 183.3).sin() * 43758.5453),
    )
}

fn frac(value: f32) -> f32 {
    value - value.floor()
}

fn saturate(value: f32) -> f32 {
    value.clamp(0.0, 1.0)
}

fn smoothstep(edge0: f32, edge1: f32, value: f32) -> f32 {
    let t = saturate((value - edge0) / (edge1 - edge0));
    t * t * (3.0 - 2.0 * t)
}

fn lerp(start: f32, end: f32, amount: f32) -> f32 {
    start + (end - start) * amount
}

fn color(r: f32, g: f32, b: f32) -> CultMathColor32 {
    CultMathColor32 {
        r: r.clamp(0.0, 255.0) as u8,
        g: g.clamp(0.0, 255.0) as u8,
        b: b.clamp(0.0, 255.0) as u8,
        a: 255,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn batch_sampler_writes_colors() {
        let xs = [0.0, 20.0, 100.0];
        let ys = [0.0, 30.0, 140.0];
        let tones = [0, 3, 4];
        let spans = [1920.0, 8.0, 8.0];
        let mut out = [CultMathColor32::default(); 3];
        let rc = unsafe {
            cultmath_apollonian_voronoi_tones(
                xs.as_ptr(),
                ys.as_ptr(),
                tones.as_ptr(),
                spans.as_ptr(),
                xs.len(),
                1080.0,
                12,
                out.as_mut_ptr(),
            )
        };
        assert_eq!(rc, 0);
        assert!(out.iter().any(|color| color.r != 0 || color.g != 0 || color.b != 0));
    }
}
