import * as React from "react"
import type { Label as LabelPrimitive } from "radix-ui"
import { Slot } from "radix-ui"
import {
  Controller,
  FormProvider,
  type ControllerProps,
  type FieldPath,
  type FieldValues,
} from "react-hook-form"

import { cn } from "@/lib/utils"
import { Label } from "@/components/ui/label"
import { FormFieldContext, FormItemContext, useFormField } from "./form-field-context"

const Form = FormProvider

const FormField = <
  TFieldValues extends FieldValues = FieldValues,
  TName extends FieldPath<TFieldValues> = FieldPath<TFieldValues>,
>({
  ...props
}: ControllerProps<TFieldValues, TName>) => {
  return (
    <FormFieldContext.Provider value={{ name: props.name }}>
      <Controller {...props} />
    </FormFieldContext.Provider>
  )
}

function FormItem({ className, ...props }: React.ComponentProps<"div">) {
  const id = React.useId()

  return (
    <FormItemContext.Provider value={{ id }}>
      <div
        // grid-cols-[minmax(0,1fr)] rather than a bare `grid`: grid children
        // default to `min-width: auto`, so a control whose min-content is
        // wider than the item (a Combobox with a long placeholder, say)
        // renders at that intrinsic width and spills out of the item — over
        // the row gap and on top of the neighbouring field. A minmax(0,…)
        // track lets controls shrink and truncate inside their own box, which
        // is what makes field overlap structurally impossible.
        data-slot="form-item"
        className={cn("grid grid-cols-[minmax(0,1fr)] gap-2", className)}
        {...props}
      />
    </FormItemContext.Provider>
  )
}

function FormLabel({
  className,
  required,
  children,
  ...props
}: React.ComponentProps<typeof LabelPrimitive.Root> & { required?: boolean }) {
  const { formItemId } = useFormField()

  return (
    <Label
      data-slot="form-label"
      className={cn(className)}
      htmlFor={formItemId}
      {...props}
    >
      {children}
      {required && <span className="text-destructive ml-0.5">*</span>}
    </Label>
  )
}

function FormControl({ ...props }: React.ComponentProps<typeof Slot.Root>) {
  const { error, formItemId, formDescriptionId, formMessageId } = useFormField()

  return (
    <Slot.Root
      data-slot="form-control"
      id={formItemId}
      aria-describedby={
        !error
          ? `${formDescriptionId}`
          : `${formDescriptionId} ${formMessageId}`
      }
      aria-invalid={!!error}
      {...props}
    />
  )
}

function FormDescription({ className, ...props }: React.ComponentProps<"p">) {
  const { formDescriptionId } = useFormField()

  return (
    <p
      data-slot="form-description"
      id={formDescriptionId}
      className={cn("text-muted-foreground text-sm", className)}
      {...props}
    />
  )
}

function FormMessage({ className, ...props }: React.ComponentProps<"p">) {
  const { error, formMessageId } = useFormField()
  const body = error ? String(error?.message ?? "") : props.children

  return (
    <p
      data-slot="form-message"
      id={formMessageId}
      role={body ? "alert" : undefined}
      aria-hidden={body ? undefined : true}
      className={cn(
        "text-destructive min-h-5 text-sm font-medium",
        className,
      )}
      {...props}
    >
      {body}
    </p>
  )
}

export {
  Form,
  FormItem,
  FormLabel,
  FormControl,
  FormDescription,
  FormMessage,
  FormField,
}
