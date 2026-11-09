use std::{env, path::PathBuf, process::Command};
fn main() {
    let out = PathBuf::from(env::var("OUT_DIR").unwrap()).join("app.res.o");
    let st = Command::new("x86_64-w64-mingw32-windres")
        .args(["app.rc", "-O", "coff", "-o"]).arg(&out).status().expect("windres");
    assert!(st.success());
    println!("cargo:rustc-link-arg={}", out.display());
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed=app.manifest");
}
