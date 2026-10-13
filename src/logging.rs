//! Лог в файл с ротацией по размеру (FR-009: `service.log` + `service.log.1`, по 5 МБ) и, по желанию, в консоль.

use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use tracing_subscriber::fmt::MakeWriter;

pub const MAX_BYTES: u64 = 5 * 1024 * 1024;

pub struct RotatingFile {
    path: PathBuf,
    max: u64,
    file: Option<File>,
    size: u64,
}

impl RotatingFile {
    pub fn open(path: &Path, max: u64) -> io::Result<Self> {
        let file = OpenOptions::new().create(true).append(true).open(path)?;
        let size = file.metadata()?.len();
        Ok(Self { path: path.to_path_buf(), max, file: Some(file), size })
    }

    fn rotate(&mut self) -> io::Result<()> {
        self.file = None;
        let old = self.path.with_extension("log.1");
        let _ = std::fs::remove_file(&old);
        std::fs::rename(&self.path, &old)?;
        self.file = Some(OpenOptions::new().create(true).append(true).open(&self.path)?);
        self.size = 0;
        Ok(())
    }
}

impl Write for RotatingFile {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        if self.size + buf.len() as u64 > self.max && self.size > 0 {
            // Не удалось повернуть — пишем дальше в старый файл, лог важнее лимита.
            let _ = self.rotate();
        }
        let f = match self.file.as_mut() {
            Some(f) => f,
            None => return Ok(buf.len()),
        };
        let n = f.write(buf)?;
        self.size += n as u64;
        Ok(n)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.file.as_mut().map_or(Ok(()), |f| f.flush())
    }
}

#[derive(Clone)]
pub struct SharedFile(Arc<Mutex<RotatingFile>>);

pub struct SharedGuard<'a>(std::sync::MutexGuard<'a, RotatingFile>);

impl Write for SharedGuard<'_> {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        self.0.write(buf)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.0.flush()
    }
}

impl<'a> MakeWriter<'a> for SharedFile {
    type Writer = SharedGuard<'a>;
    fn make_writer(&'a self) -> Self::Writer {
        SharedGuard(self.0.lock().unwrap_or_else(|p| p.into_inner()))
    }
}

/// Сервис: лог в `logs\service.log`. Команды с консолью: ещё и в stderr.
pub fn init(logs_dir: Option<&Path>, console: bool, verbose: bool) {
    use tracing_subscriber::prelude::*;
    let level = if verbose { tracing::Level::DEBUG } else { tracing::Level::INFO };
    let filter = tracing_subscriber::filter::LevelFilter::from_level(level);

    let file_layer = logs_dir.and_then(|dir| {
        std::fs::create_dir_all(dir).ok()?;
        let f = RotatingFile::open(&dir.join("service.log"), MAX_BYTES).ok()?;
        Some(
            tracing_subscriber::fmt::layer()
                .with_ansi(false)
                .with_target(false)
                .with_writer(SharedFile(Arc::new(Mutex::new(f)))),
        )
    });
    let console_layer = console
        .then(|| tracing_subscriber::fmt::layer().with_target(false).without_time().with_writer(io::stderr));
    let _ = tracing_subscriber::registry().with(filter).with(file_layer).with(console_layer).try_init();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rotates_at_limit_and_keeps_one_old_file() {
        let dir = std::env::temp_dir().join(format!("bw-log-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("service.log");
        let mut f = RotatingFile::open(&p, 100).unwrap();
        for _ in 0..5 {
            f.write_all(&[b'x'; 60]).unwrap();
        }
        f.flush().unwrap();
        drop(f);
        assert_eq!(std::fs::metadata(&p).unwrap().len(), 60);
        assert_eq!(std::fs::metadata(p.with_extension("log.1")).unwrap().len(), 60);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
