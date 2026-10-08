fn main() {
    // The viewer front end is compiled into the executable, so an edit to it
    // has to count as a change to this crate.
    println!("cargo:rerun-if-changed=../../app");
    println!("cargo:rerun-if-changed=../../vendor");
    tauri_build::build()
}
