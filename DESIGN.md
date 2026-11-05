---
name: PURSOV STUDIO
description: Нативная Windows-оболочка с одним главным действием
colors:
  primary: "#176D3E"
  primary-dark: "#68D994"
  ground: "#F7F8F7"
  ground-dark: "#171C19"
  surface: "#FFFFFF"
  surface-dark: "#202722"
  ink: "#18211C"
  ink-dark: "#EFF5F0"
  muted: "#58635C"
  muted-dark: "#B6C2B9"
  line: "#DCE2DD"
  line-dark: "#3A473E"
  failure: "#AD302B"
  failure-dark: "#FFA69D"
typography:
  title:
    fontFamily: "Segoe UI Variable, Segoe UI"
    fontSize: "28px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI Variable, Segoe UI"
    fontSize: "14px"
    lineHeight: 1.5
  caption:
    fontFamily: "Segoe UI Variable, Segoe UI"
    fontSize: "12px"
rounded:
  button: "6px"
  panel: "8px"
spacing:
  tight: "8px"
  regular: "14px"
  section: "24px"
  page: "28px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    rounded: "{rounded.button}"
    height: "48px"
    padding: "12px 24px"
---

# PURSOV STUDIO

## Overview

Рабочий интерфейс Windows с коротким главным экраном. Подробности открываются по запросу. Штатные элементы Fluent задают поведение полей, меню и навигации.

## Colors

Цвета переключаются вместе с системной светлой или тёмной темой. В высококонтрастном режиме используются SystemColors. Зелёный выделяет главное действие и подтверждённый ответ. Неизвестный результат имеет нейтральный цвет, ошибочный — красный. Текст и знак сохраняют смысл без различения цветов.

## Typography

Segoe UI Variable с запасным Segoe UI. Основной текст 14 DIP, статус 28 DIP, пояснения 12 DIP. Consolas используется только для журнала.

## Layout

Начальный размер окна 620 × 650 DIP с ограничением рабочей областью монитора. Минимальная ширина 360 DIP. Внешний отступ 28 DIP, главный текст и кнопка выровнены по левому краю. На небольшом экране доступна вертикальная прокрутка. Раздел подробностей открывается в том же окне, кнопка «Назад» возвращает к главному экрану.

## Elevation & Depth

Панель подробностей выделяется сплошной поверхностью и тонкой рамкой. Заголовок и кнопки окна остаются системными.

## Shapes

Знак состоит из двух соединённых форм. Векторный оригинал находится в Assets/App.svg. ICO содержит размеры 16, 20, 24, 32, 40, 48, 64 и 256 px. Состояния трея различаются модификатором формы и подписью.

## Components

Главная кнопка меняется вместе с состоянием контроллера. Смена текста состояния сопровождается затуханием длительностью 170 мс, если разрешена анимация Windows. При скрытом окне анимация останавливается. Журнал обновляется только в открытом разделе. Кнопки и ссылки имеют клавиатурный фокус, состояние объявляется через UI Automation.

Цветовые значения реализованы в UiPalette.cs, стили — в App.xaml. Связь окна и трея с состояниями контроллера реализована в MainWindow.xaml.cs. Проверки представления и WPF-разметки находятся в tests/ui-presentation и tests/ui-layout.
