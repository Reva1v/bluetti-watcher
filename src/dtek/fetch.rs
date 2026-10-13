//! Единственный внешний запрос программы (FR-001): HTTPS GET набора через WinRT `HttpClient`, системный TLS.
//! Без перенаправлений и кэша WinINet, `If-None-Match` по сохранённому ETag, ответ ≤ 4 МБ, таймаут 30 с.

use std::time::Duration;

pub const DEFAULT_HOST: &str = "raw.githubusercontent.com";
pub const MAX_BODY: usize = 4 * 1024 * 1024;
pub const TIMEOUT: Duration = Duration::from_secs(30);

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Fetched {
    /// 304: набор не менялся.
    NotModified,
    Body {
        body: Vec<u8>,
        etag: Option<String>,
    },
}

/// Адрес набора: `[dtek] url` или файл региона на GitHub.
pub fn dataset_url(region: &str, url_override: Option<&str>) -> String {
    match url_override {
        Some(u) => u.to_string(),
        None => format!("https://{DEFAULT_HOST}/Baskerville42/outage-data-ua/main/data/{region}.json"),
    }
}

/// Только `https://`; без переопределения — только `raw.githubusercontent.com`.
pub fn check_url(url: &str, overridden: bool) -> Result<(), String> {
    let rest = url.strip_prefix("https://").ok_or("адрес набора не https://")?;
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    if authority.is_empty() || authority.contains('@') {
        return Err("неверный адрес набора".into());
    }
    let host = authority.split(':').next().unwrap_or("");
    if !overridden && !host.eq_ignore_ascii_case(DEFAULT_HOST) {
        return Err(format!("хост {host} не {DEFAULT_HOST}"));
    }
    Ok(())
}

/// Источник набора: WinRT в проде, фейк в тестах.
#[allow(async_fn_in_trait)]
pub trait Source {
    async fn get(&mut self, url: &str, etag: Option<&str>) -> Result<Fetched, String>;
}

#[cfg(windows)]
pub struct WinRtSource;

#[cfg(windows)]
impl Source for WinRtSource {
    async fn get(&mut self, url: &str, etag: Option<&str>) -> Result<Fetched, String> {
        let deadline = tokio::time::Instant::now() + TIMEOUT;
        winrt::get(url, etag, deadline).await
    }
}

#[cfg(windows)]
mod winrt {
    use std::future::IntoFuture;

    use tokio::time::Instant;
    use windows::Foundation::Uri;
    use windows::Storage::Streams::{Buffer, DataReader, InputStreamOptions};
    use windows::Web::Http::Filters::{
        HttpBaseProtocolFilter, HttpCacheReadBehavior, HttpCacheWriteBehavior,
    };
    use windows::Web::Http::{HttpClient, HttpCompletionOption, HttpMethod, HttpRequestMessage};
    use windows::core::HSTRING;

    use super::{Fetched, MAX_BODY};

    const CHUNK: u32 = 64 * 1024;

    fn err(what: &str) -> impl Fn(windows::core::Error) -> String + '_ {
        move |e| format!("{what}: {}", e.message())
    }

    /// Дождаться WinRT-операции до `deadline`; по таймауту операция отменяется (`cancel`).
    async fn within<O: IntoFuture>(
        op: O,
        cancel: impl FnOnce(),
        deadline: Instant,
        what: &str,
    ) -> Result<O::Output, String> {
        match tokio::time::timeout_at(deadline, op.into_future()).await {
            Ok(r) => Ok(r),
            Err(_) => {
                cancel();
                Err(format!("{what}: таймаут 30 с"))
            }
        }
    }

    /// `within` для операции с методом `Cancel`.
    macro_rules! wait {
        ($op:expr, $deadline:expr, $what:expr) => {{
            let op = $op;
            let c = op.clone();
            within(
                op,
                move || {
                    let _ = c.Cancel();
                },
                $deadline,
                $what,
            )
            .await?
            .map_err(err($what))?
        }};
    }

    pub async fn get(url: &str, etag: Option<&str>, deadline: Instant) -> Result<Fetched, String> {
        let filter = HttpBaseProtocolFilter::new().map_err(err("HTTP-фильтр"))?;
        filter.SetAllowAutoRedirect(false).map_err(err("HTTP-фильтр"))?;
        filter.SetAllowUI(false).map_err(err("HTTP-фильтр"))?;
        let cache = filter.CacheControl().map_err(err("HTTP-кэш"))?;
        cache.SetReadBehavior(HttpCacheReadBehavior::NoCache).map_err(err("HTTP-кэш"))?;
        cache.SetWriteBehavior(HttpCacheWriteBehavior::NoCache).map_err(err("HTTP-кэш"))?;
        let client = HttpClient::Create(&filter).map_err(err("HttpClient"))?;
        let uri = Uri::CreateUri(&HSTRING::from(url)).map_err(err("адрес"))?;
        let req = HttpRequestMessage::Create(&HttpMethod::Get().map_err(err("GET"))?, &uri)
            .map_err(err("запрос"))?;
        if let Some(e) = etag {
            let h = req.Headers().map_err(err("заголовки"))?;
            h.TryAppendWithoutValidation(&HSTRING::from("If-None-Match"), &HSTRING::from(e))
                .map_err(err("If-None-Match"))?;
        }
        let op = client
            .SendRequestWithOptionAsync(&req, HttpCompletionOption::ResponseHeadersRead)
            .map_err(err("запрос"))?;
        let resp = wait!(op, deadline, "запрос");
        let status = resp.StatusCode().map_err(err("ответ"))?.0;
        if status == 304 {
            let _ = resp.Close();
            let _ = client.Close();
            return Ok(Fetched::NotModified);
        }
        if status != 200 {
            let _ = resp.Close();
            let _ = client.Close();
            return Err(format!("HTTP {status}"));
        }
        let headers = resp.Headers().map_err(err("заголовки ответа"))?;
        let etag_name = HSTRING::from("ETag");
        let etag = match headers.HasKey(&etag_name) {
            Ok(true) => headers.Lookup(&etag_name).ok().map(|v| v.to_string()).filter(|v| v.len() <= 200),
            _ => None,
        };
        let content = resp.Content().map_err(err("тело"))?;
        if let Ok(len) = content.Headers().and_then(|h| h.ContentLength()).and_then(|r| r.Value())
            && len > MAX_BODY as u64
        {
            let _ = resp.Close();
            let _ = client.Close();
            return Err(format!("ответ {len} байт > 4 МБ"));
        }
        let stream = wait!(content.ReadAsInputStreamAsync().map_err(err("тело"))?, deadline, "тело");
        let mut body = Vec::new();
        loop {
            let buf = Buffer::Create(CHUNK).map_err(err("буфер"))?;
            let op = stream.ReadAsync(&buf, CHUNK, InputStreamOptions::Partial).map_err(err("чтение"))?;
            let got = wait!(op, deadline, "чтение");
            let n = got.Length().map_err(err("чтение"))? as usize;
            if n == 0 {
                break;
            }
            if body.len() + n > MAX_BODY {
                let _ = resp.Close();
                let _ = client.Close();
                return Err("ответ больше 4 МБ".into());
            }
            let mut chunk = vec![0u8; n];
            DataReader::FromBuffer(&got).and_then(|r| r.ReadBytes(&mut chunk)).map_err(err("чтение"))?;
            body.extend_from_slice(&chunk);
        }
        let _ = resp.Close();
        let _ = client.Close();
        Ok(Fetched::Body { body, etag })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn urls() {
        let u = dataset_url("kyiv", None);
        assert_eq!(u, "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv.json");
        assert!(check_url(&u, false).is_ok());
        assert!(check_url("http://raw.githubusercontent.com/x.json", false).is_err());
        assert!(check_url("https://evil.example/x.json", false).is_err());
        assert!(check_url("https://raw.githubusercontent.com@evil.example/x", false).is_err());
        assert!(check_url("https://evil.example/x.json", true).is_ok());
        assert!(check_url("http://evil.example/x.json", true).is_err());
        assert!(check_url("https:///x", true).is_err());
        assert_eq!(dataset_url("kyiv", Some("https://e.org/k.json")), "https://e.org/k.json");
    }

    /// Реальный запрос к GitHub: `cargo test -- --ignored real_fetch`.
    #[cfg(windows)]
    #[tokio::test]
    #[ignore]
    async fn real_fetch_parses() {
        let url = dataset_url("kyiv", None);
        let mut src = WinRtSource;
        let Fetched::Body { body, etag } = src.get(&url, None).await.unwrap() else {
            panic!("304 без ETag")
        };
        let s = crate::dtek::parse::parse(&body).unwrap();
        println!(
            "bytes {} etag {etag:?} update {} collected {} queues {} days {:?}",
            body.len(),
            s.update,
            crate::clock::to_local_iso(s.collected),
            s.days.len(),
            s.days
                .get("GPV9.1")
                .map(|d| d.iter().map(|d| crate::clock::to_local_iso(d.start)).collect::<Vec<_>>())
        );
        assert!(s.days.contains_key("GPV9.1"));
        let w = crate::dtek::windows::off_windows(s.queue_days("GPV9.1"));
        let fmt = crate::clock::to_local_short;
        println!("GPV9.1 off: {:?}", w.iter().map(|w| (fmt(w.start), fmt(w.end))).collect::<Vec<_>>());
        if let Some(e) = etag {
            assert_eq!(src.get(&url, Some(&e)).await.unwrap(), Fetched::NotModified);
            println!("304 с If-None-Match: да");
        }
    }
}
