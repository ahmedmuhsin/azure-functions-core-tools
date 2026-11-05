// Provisional. This file is written against the unpublished Azure Functions Rust crate.
// Its API is not final, so this file may change.

use azure_functions_rust::prelude::*;

// The route is hello/{name?}, so /api/hello/Ada answers "hello Ada" and /api/hello answers "hello world".
async fn hello(request: HttpRequest, _ctx: Context) -> Result<HttpResponse, Error> {
    let name = request.route_param("name").unwrap_or("world");
    Ok(HttpResponse::text(200, format!("hello {name}")))
}

fn main() -> Result<(), Error> {
    // Register the functions of the app here. The crate reads the arguments that the worker is started with.
    app()
        .http(
            "hello",
            HttpTrigger::new()
                .route("hello/{name?}")
                .auth_level(AuthLevel::Anonymous),
            hello,
        )
        .run()
}
