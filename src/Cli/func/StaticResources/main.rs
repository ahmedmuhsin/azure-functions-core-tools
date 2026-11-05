// Provisional. This file is written against the unpublished Azure Functions Rust SDK and worker crates.
// Their API is not final, so this file may change.

use std::collections::BTreeMap;

use azure_functions_rust_sdk::{
    ApplicationBuilder, BindingDescriptor as B, BindingValue as V, FunctionDescriptor,
    HttpContract, Invocation, InvocationError, InvocationResult,
    http::{HttpBody, HttpRequest, HttpResponse},
};
use azure_functions_rust_worker::{Config, StartupArguments, Worker, run};

// The HTTP request is decoded from the invocation's "req" input.
struct Request(HttpRequest);

impl TryFrom<Invocation> for Request {
    type Error = InvocationError;

    fn try_from(invocation: Invocation) -> Result<Self, Self::Error> {
        Ok(Self(HttpRequest::from_invocation(&invocation, "req")?))
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Where to connect, and the ids of the worker, come from the arguments that the worker is started with.
    // StartupArguments reads them, in both spellings that a launcher passes.
    let startup = StartupArguments::from_env()?;
    let config = Config::from_proxy_setting(None)?.with_startup_arguments(&startup);

    // Register the functions of the app here. The response comes back as the return value.
    let mut builder = ApplicationBuilder::new();
    builder.register(
        FunctionDescriptor::builder("hello")
            .http_contract(HttpContract::Canonical)
            .binding(
                B::trigger("req", "httpTrigger")
                    .property("authLevel", "anonymous".into())
                    .property("methods", serde_json::json!(["get", "post"])),
            )
            .build()?,
        |Request(request): Request| async move {
            println!("Processing HTTP trigger for {}", request.url);
            let response = HttpResponse {
                status_code: 200,
                headers: BTreeMap::from([(
                    "content-type".into(),
                    Some(vec!["text/plain; charset=utf-8".into()]),
                )]),
                body: Some(HttpBody::Text("Hello from Rust Worker!".into())),
            };
            let response = V::try_from(response).map_err(|error| InvocationError::Handler {
                message: error.to_string(),
            })?;
            Ok(InvocationResult {
                return_value: Some(response),
            })
        },
    )?;

    let app = builder.build();
    let worker = Worker::new(app.functions().cloned().collect(), app, config).await?;
    run(
        worker,
        startup.endpoint(),
        startup.worker_id().to_owned(),
        startup.request_id().to_owned(),
    )
    .await
}
