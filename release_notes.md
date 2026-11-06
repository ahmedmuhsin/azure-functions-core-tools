# Azure Functions CLI 4.16.0

#### Host Version

- Host Runtime Version: 4.1054.250
- In-Proc CLI:
  - CLI Version: 4.9.0
  - Host Runtime Version: 4.52.200 (includes 4.852.200, 4.652.200)

#### Changes
- Add a preview of Rust apps to `func init`, `func start` and `func pack`. Build and publish behavior may change. `func init` writes `FUNCTIONS_WORKER_RUNTIME=native` and no setting for the language, which comes from `Cargo.toml`. `func start` builds into `bin/app` (`bin\app.exe` on Windows), the executable that the native worker config names, and `func start --no-build` needs no Rust toolchain. `func pack` builds a release binary into `bin/app`, over a debug build of `func start`, and `func pack --no-build` warns when `bin/app` has debug information.
