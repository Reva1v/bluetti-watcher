//! Фоновый монитор станции BLUETTI AC180P: ядро, общее для exe и интеграционных тестов.

// Модули подключаются по мере реализации задач; снять перед T055.
#![allow(dead_code)]

pub mod ble;
pub mod clock;
pub mod config;
pub mod console;
pub mod logging;
pub mod model;
pub mod paths;
