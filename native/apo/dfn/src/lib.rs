// Red Tune's door to DeepFilterNet 3 (libDF in DeepFilterNet, MIT/Apache-2.0): the model built in, five plain C functions.
// Built by ..\build.ps1 (cargo) into a static library linked into RedTuneApo.dll; see rtdsp.h for how it is used.
use df::tract::{DfParams, DfTract, ReduceMask, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

pub struct RtDf { m: DfTract }

/// The model and its state; NULL if it could not be made. atten_lim_db: how deep the noise may go (100 = no limit).
#[no_mangle]
pub extern "C" fn rtdf_create(atten_lim_db: f32) -> *mut RtDf {
    let r = std::panic::catch_unwind(|| {
        let rp = RuntimeParams::default_with_ch(1)
            .with_atten_lim(atten_lim_db)
            .with_thresholds(-15.0, 35.0, 35.0)
            .with_post_filter(0.0)
            .with_mask_reduce(ReduceMask::MAX);
        DfTract::new(DfParams::default(), &rp).ok()
    });
    match r { Ok(Some(m)) => Box::into_raw(Box::new(RtDf { m })), _ => std::ptr::null_mut() }
}

/// Samples per frame (480 at 48 kHz).
#[no_mangle]
pub unsafe extern "C" fn rtdf_frame(st: *mut RtDf) -> usize { match st.as_ref() { Some(s) => s.m.hop_size, None => 0 } }

/// One frame through the model: input and output of rtdf_frame() samples (-1..1). Returns the local SNR, dB.
#[no_mangle]
pub unsafe extern "C" fn rtdf_process(st: *mut RtDf, input: *const f32, output: *mut f32) -> f32 {
    let s = match st.as_mut() { Some(s) => s, None => return 0.0 };
    let n = s.m.hop_size;
    let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let i = ArrayView2::from_shape_ptr((1, n), input);
        let o = ArrayViewMut2::from_shape_ptr((1, n), output);
        s.m.process(i, o).unwrap_or(0.0)
    }));
    match r { Ok(v) => v, Err(_) => { std::ptr::copy_nonoverlapping(input, output, n); -100.0 } }
}

#[no_mangle]
pub unsafe extern "C" fn rtdf_set_atten(st: *mut RtDf, lim_db: f32) { if let Some(s) = st.as_mut() { s.m.set_atten_lim(lim_db) } }

#[no_mangle]
pub unsafe extern "C" fn rtdf_free(st: *mut RtDf) { if !st.is_null() { drop(Box::from_raw(st)) } }
