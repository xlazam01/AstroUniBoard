// AstroUniBoard system clock (ported from OpenWheel fw/src/clock_config.cpp)
// 8 MHz crystal Y1 (HSE) -> PLL -> SYSCLK 64 MHz, USB 48 MHz from PLLQ.
// Overrides the weak SystemClock_Config() of the btt_ebb42_v1_1 variant, which runs SYSCLK from
// HSE but leaves USB on the reset default HSI48, untrimmed (outside the USB +-0.25 % tolerance),
// and hangs in Error_Handler() if the crystal does not start.
// If the crystal does not start, fall back to HSI and trim HSI48 from the USB SOF packets (CRS).
#include <Arduino.h>

static bool configBuses() {
    RCC_ClkInitTypeDef clk = {};
    clk.ClockType = RCC_CLOCKTYPE_HCLK | RCC_CLOCKTYPE_SYSCLK | RCC_CLOCKTYPE_PCLK1;
    clk.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
    clk.AHBCLKDivider = RCC_SYSCLK_DIV1;
    clk.APB1CLKDivider = RCC_HCLK_DIV1;
    return HAL_RCC_ClockConfig(&clk, FLASH_LATENCY_2) == HAL_OK;
}

static bool clockFromHSE() {
    RCC_OscInitTypeDef osc = {};
    osc.OscillatorType = RCC_OSCILLATORTYPE_HSE;
    osc.HSEState = RCC_HSE_ON;
    osc.PLL.PLLState = RCC_PLL_ON;
    osc.PLL.PLLSource = RCC_PLLSOURCE_HSE;
    osc.PLL.PLLM = RCC_PLLM_DIV1;        // 8 MHz
    osc.PLL.PLLN = 24;                   // VCO 192 MHz
    osc.PLL.PLLP = RCC_PLLP_DIV2;        // 96 MHz (unused)
    osc.PLL.PLLQ = RCC_PLLQ_DIV4;        // 48 MHz USB
    osc.PLL.PLLR = RCC_PLLR_DIV3;        // 64 MHz SYSCLK
    if (HAL_RCC_OscConfig(&osc) != HAL_OK || !configBuses()) return false;

    RCC_PeriphCLKInitTypeDef per = {};
    per.PeriphClockSelection = RCC_PERIPHCLK_USB;
    per.UsbClockSelection = RCC_USBCLKSOURCE_PLL;
    return HAL_RCCEx_PeriphCLKConfig(&per) == HAL_OK;
}

static void clockFromHSI() {
    RCC_OscInitTypeDef osc = {};
    osc.OscillatorType = RCC_OSCILLATORTYPE_HSI | RCC_OSCILLATORTYPE_HSI48 | RCC_OSCILLATORTYPE_HSE;
    osc.HSEState = RCC_HSE_OFF;
    osc.HSIState = RCC_HSI_ON;
    osc.HSI48State = RCC_HSI48_ON;
    osc.HSIDiv = RCC_HSI_DIV1;
    osc.HSICalibrationValue = RCC_HSICALIBRATION_DEFAULT;
    osc.PLL.PLLState = RCC_PLL_ON;
    osc.PLL.PLLSource = RCC_PLLSOURCE_HSI;
    osc.PLL.PLLM = RCC_PLLM_DIV1;        // 16 MHz
    osc.PLL.PLLN = 8;                    // VCO 128 MHz
    osc.PLL.PLLP = RCC_PLLP_DIV2;
    osc.PLL.PLLQ = RCC_PLLQ_DIV2;
    osc.PLL.PLLR = RCC_PLLR_DIV2;        // 64 MHz SYSCLK
    if (HAL_RCC_OscConfig(&osc) != HAL_OK || !configBuses()) Error_Handler();

    RCC_PeriphCLKInitTypeDef per = {};
    per.PeriphClockSelection = RCC_PERIPHCLK_USB;
    per.UsbClockSelection = RCC_USBCLKSOURCE_HSI48;
    if (HAL_RCCEx_PeriphCLKConfig(&per) != HAL_OK) Error_Handler();

    // Trim HSI48 continuously from the 1 kHz USB start-of-frame
    __HAL_RCC_CRS_CLK_ENABLE();
    RCC_CRSInitTypeDef crs = {};
    crs.Prescaler = RCC_CRS_SYNC_DIV1;
    crs.Source = RCC_CRS_SYNC_SOURCE_USB;
    crs.Polarity = RCC_CRS_SYNC_POLARITY_RISING;
    crs.ReloadValue = __HAL_RCC_CRS_RELOADVALUE_CALCULATE(48000000, 1000);
    crs.ErrorLimitValue = RCC_CRS_ERRORLIMIT_DEFAULT;
    crs.HSI48CalibrationValue = RCC_CRS_HSI48CALIBRATION_DEFAULT;
    HAL_RCCEx_CRSConfig(&crs);
}

extern "C" void SystemClock_Config(void) {
    HAL_PWREx_ControlVoltageScaling(PWR_REGULATOR_VOLTAGE_SCALE1);
    if (!clockFromHSE()) clockFromHSI();
}
