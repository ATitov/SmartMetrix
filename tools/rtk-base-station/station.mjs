import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

export class Station {
  constructor(file, now = () => Date.now()) {
    this.file = file; this.now = now;
    this.saved = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : { coordinates: null, profiles: [], logs: [], settings: { mountpoint: 'UM982_BASE', port: 2101 } };
    this.usb = true; this.signal = true; this.running = false; this.survey = null; this.candidate = null; this.relocating = false;
    this.log('Приложение запущено. Симулятор; выдача остановлена.');
  }
  persist() { fs.mkdirSync(path.dirname(this.file), { recursive: true }); fs.writeFileSync(this.file + '.tmp', JSON.stringify(this.saved, null, 2)); fs.renameSync(this.file + '.tmp', this.file); }
  log(message) { this.saved.logs.unshift({ time: new Date(this.now()).toISOString(), message }); this.saved.logs = this.saved.logs.slice(0, 200); this.persist(); }
  coordinates(input) {
    const c = { lat: Number(input.lat), lon: Number(input.lon), height: Number(input.height), source: 'manual' };
    for (const key of ['lat', 'lon', 'height']) if (input[key] === '' || input[key] == null || !Number.isFinite(c[key])) throw Error('Заполните все координаты конечными числами.');
    if (Math.abs(c.lat) > 90 || Math.abs(c.lon) > 180 || c.height < -1000 || c.height > 10000) throw Error('Координаты вне допустимого диапазона.');
    return c;
  }
  status() {
    if (this.survey && this.now() - this.survey.started >= this.survey.duration * 1000) {
      this.candidate = { lat: 42.8746213, lon: 74.5698124, height: 812.43, source: 'simulated-average' };
      this.survey = null; this.log('Демонстрационное усреднение завершено. Ожидается подтверждение координат.');
    }
    const active = this.running && this.usb && this.signal;
    return { ...this.saved, simulator: true, usb: this.usb, signal: this.signal, running: active, relocating: this.relocating, candidate: this.candidate,
      survey: this.survey ? { ...this.survey, progress: Math.min(100, Math.floor((this.now() - this.survey.started) / (this.survey.duration * 10))) } : null,
      satellites: this.usb && this.signal ? 38 : 0, rate: active ? 1840 + Math.round(80 * Math.sin(this.now() / 3000)) : 0,
      clients: active ? [{ name: 'Rover Pi · демонстрация', ip: '192.168.1.52' }] : [],
      messages: [1006, 1033, 1074, 1084, 1094, 1124] };
  }
  action(action, data = {}) {
    this.status();
    switch (action) {
      case 'stop': this.running = false; this.log('Выдача остановлена оператором.'); break;
      case 'start':
        if (!this.saved.coordinates || this.relocating || this.survey || this.candidate) throw Error('Сначала подтвердите координаты места установки.');
        if (!this.usb || !this.signal) throw Error('Нет USB-соединения или спутниковых данных.');
        this.running = true; this.log('Демонстрационная выдача запущена. Реальный RTCM не передаётся.'); break;
      case 'relocate':
        this.running = false; this.relocating = true; this.saved.coordinates = null; this.candidate = null; this.survey = null;
        this.log('Начат перенос. Старые координаты сняты, выдача заблокирована.'); break;
      case 'manual':
        if (this.running) throw Error('Остановите выдачу перед изменением координат.');
        this.candidate = this.coordinates(data); this.survey = null; this.log('Новые координаты подготовлены к проверке.'); break;
      case 'survey':
        if (this.running || !this.usb || !this.signal) throw Error('Остановите выдачу и восстановите USB и спутниковые данные.');
        if (![15, 60, 300].includes(Number(data.duration))) throw Error('Недопустимая длительность усреднения.');
        this.candidate = null; this.survey = { started: this.now(), duration: Number(data.duration) }; this.log('Запущено демонстрационное усреднение.'); break;
      case 'cancel-survey': this.survey = null; this.candidate = null; this.log('Подготовка координат отменена.'); break;
      case 'confirm':
        if (!this.candidate || !this.usb || !this.signal) throw Error('Нет результата для подтверждения или отсутствует связь с приёмником.');
        this.saved.coordinates = this.candidate; this.candidate = null; this.relocating = false; this.log('Координаты подтверждены. Можно запустить выдачу.'); break;
      case 'save-profile': {
        if (!this.saved.coordinates) throw Error('Сначала подтвердите координаты.');
        const name = String(data.name || '').trim(); if (!name || name.length > 80) throw Error('Название должно содержать от 1 до 80 символов.');
        if (this.saved.profiles.length >= 100) throw Error('Достигнут лимит 100 мест.');
        this.saved.profiles.push({ id: randomUUID(), name, coordinates: { ...this.saved.coordinates } }); this.log('Сохранено место: ' + name); break;
      }
      case 'load-profile': {
        if (this.running) throw Error('Остановите выдачу перед выбором места.');
        const p = this.saved.profiles.find(p => p.id === data.id); if (!p) throw Error('Место не найдено.');
        this.candidate = { ...p.coordinates }; this.survey = null; this.log('Выбрано место: ' + p.name + '. Проверьте положение антенны.'); break;
      }
      case 'delete-profile': this.saved.profiles = this.saved.profiles.filter(p => p.id !== data.id); this.log('Профиль места удалён.'); break;
      case 'settings':
        if (this.running) throw Error('Остановите выдачу перед изменением настроек.');
        if (!/^[A-Za-z0-9_-]{1,32}$/.test(data.mountpoint || '') || !Number.isInteger(Number(data.port)) || Number(data.port) < 1024 || Number(data.port) > 65535) throw Error('Укажите mountpoint латиницей и порт 1024–65535.');
        this.saved.settings = { mountpoint: data.mountpoint, port: Number(data.port) }; this.log('Настройки демонстрационного NTRIP сохранены.'); break;
      case 'fault':
        if (!['usb', 'signal'].includes(data.kind)) throw Error('Неизвестный сценарий.');
        this[data.kind] = !this[data.kind];
        if (!this.usb || !this.signal) { this.running = false; this.survey = null; this.candidate = null; }
        this.log((data.kind === 'usb' ? 'USB' : 'Спутниковые данные') + (this[data.kind] ? ': восстановлено; запуск вручную.' : ': потеря; выдача и усреднение остановлены.')); break;
      default: throw Error('Неизвестная команда.');
    }
    this.persist(); return this.status();
  }
}
