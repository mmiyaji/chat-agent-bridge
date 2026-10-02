import { expect, test } from "@playwright/test";

test("admin dashboard renders state and clears approvals after approve", async ({ page }) => {
  await page.goto("/admin/legacy");

  await expect(page.getByTestId("sessions-table")).toContainText("session-e2e-1");
  await expect(page.getByTestId("approvals-table")).toContainText("Refine admin screen");
  await expect(page.getByTestId("failures-table")).toContainText("Previous run failed");

  await page.locator("#detail-drawer").getByRole("button", { name: "Approve", exact: true }).click();
  await page.locator("#detail-close").click();

  await expect(page.getByTestId("approvals-table-empty")).toBeVisible();
});

test("admin dashboard stays within a narrow viewport", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/admin/legacy");

  const overflow = await page.evaluate(() => {
    return document.documentElement.scrollWidth - document.documentElement.clientWidth;
  });

  expect(overflow).toBeLessThanOrEqual(1);
});
