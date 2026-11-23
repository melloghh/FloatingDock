fn main() {
    // the icon (the tray icon is taken from the executable) and the manifest (per-monitor DPI awareness)
    println!("cargo:rerun-if-changed=resources");
    embed_resource::compile("resources/fd-engine.rc", embed_resource::NONE).manifest_required().unwrap();
}
